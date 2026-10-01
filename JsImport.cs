using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Demo3D.Mcp;
using Demo3D.Visuals;
using ModelContextProtocol.Server;

namespace Demo3D.Components {

    /// <summary>
    /// Manifest import (stage 3, task 2.13): lands the files of a js_export run back into the model.
    ///
    /// Landing order: an explicit `map=` entry wins, then the script key/GUID recorded in the
    /// manifest, then a unique name. Anything else is SKIPPED with the reason and a suggestion -
    /// this tool never guesses silently (QLP design T11). `dryRun` defaults to TRUE.
    ///
    /// Before the first real write every target that already exists is captured into ONE snapshot
    /// tag, so a whole import is undone by a single `js_rollback(snapshot="last")`.
    /// A file whose rev differs from the manifest rev is reported as "(file edited after export)"
    /// and its CURRENT content is what gets imported.
    /// </summary>
    internal static class JsImportKernel {

        internal class Entry {
            public string Name;
            public string Key;
            public string File;
            public string Rev;
            public int Chars;
        }

        /// <summary>Reads back a manifest written by js_export (own format, parsed conservatively).</summary>
        public static List<Entry> ParseManifest(string text, out string error) {
            error = null;
            var list = new List<Entry>();
            if (string.IsNullOrEmpty(text)) { error = "manifest is empty"; return list; }
            var array = Regex.Match(text, @"""scripts""\s*:\s*\[(.*)\]\s*\}\s*$", RegexOptions.Singleline);
            if (!array.Success) { error = "manifest has no \"scripts\" array (not a js-export.json?)"; return list; }
            foreach (Match m in Regex.Matches(array.Groups[1].Value, @"\{(?:[^{}]|\{[^{}]*\})*\}")) {
                var o = m.Value;
                var e = new Entry {
                    Name = Field(o, "name"),
                    Key = Field(o, "key"),
                    File = Field(o, "file"),
                    Rev = Field(o, "rev")
                };
                var chars = Field(o, "chars");
                int n;
                if (int.TryParse(chars, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) e.Chars = n;
                if (!string.IsNullOrEmpty(e.Name) && !string.IsNullOrEmpty(e.File)) list.Add(e);
            }
            if (list.Count == 0) error = "the \"scripts\" array holds no usable entry (name+file required)";
            return list;
        }

        static string Field(string obj, string name) {
            var m = Regex.Match(obj, "\"" + name + "\"\\s*:\\s*\"([^\"]*)\"");
            if (m.Success) return Unescape(m.Groups[1].Value);
            m = Regex.Match(obj, "\"" + name + "\"\\s*:\\s*(-?\\d+)");
            return m.Success ? m.Groups[1].Value : null;
        }

        static string Unescape(string s) {
            return s.Replace("\\r", "\r").Replace("\\n", "\n").Replace("\\t", "\t").Replace("\\\"", "\"").Replace("\\\\", "\\");
        }
    }

    public partial class JsScriptTools {

        [McpServerTool]
        [Description("Import JScript sources from a js_export manifest folder. Landing order: explicit map= wins, then the manifest key/GUID, then a unique name; everything else is SKIPped with a reason (never silently). dryRun defaults to TRUE. Every overwritten target is captured into one snapshot tag first, so the whole import is undone with js_rollback(snapshot=\"last\"). Files whose rev differs from the manifest are reported as edited after export, and their current content is imported.")]
        public string JsImport(
            [Description("Export folder (uses its js-export.json) or the manifest file path")] string from,
            [Description("Comma-separated script names to import, empty for every entry in the manifest")] string targets = "",
            [Description("Landing override 'manifestName=targetScript' pairs, comma separated; wins over key/name")] string map = "",
            [Description("Report what would happen, write nothing (default true)")] bool dryRun = true,
            [Description("Create the script when the target does not exist yet")] bool createIfMissing = false,
            [Description("Write sources that lint rejects (error level)")] bool force = false,
            [Description("Take one snapshot tag for the whole import so js_rollback(last) undoes it")] bool autoSnapshot = true,
            [Description("After writing, press Reset once and scan the engine log for compile failures of the imported scripts")] bool verify = false) {
            if (string.IsNullOrEmpty(from)) return "ERROR: 'from' is required (export folder or manifest path).";

            return ExecuteMcpCommand(McpPermission.AllowScriptModification, app => {
                var doc = app.Document;

                string manifestPath = from;
                if (Directory.Exists(from)) manifestPath = Path.Combine(from, "js-export.json");
                else if (!File.Exists(from) && File.Exists(Path.Combine(from, "js-export.json"))) manifestPath = Path.Combine(from, "js-export.json");
                if (!File.Exists(manifestPath)) return "ERROR: manifest not found at '" + manifestPath + "'.";
                var folder = Path.GetDirectoryName(manifestPath);

                string text;
                try { text = File.ReadAllText(manifestPath); }
                catch (Exception ex) { return "ERROR reading '" + manifestPath + "': " + ex.Message; }

                string parseError;
                var entries = JsImportKernel.ParseManifest(text, out parseError);
                if (parseError != null) return "ERROR: " + parseError;

                var wanted = JsExportKernel.SplitCsv(targets);
                var mapPairs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var pair in JsExportKernel.SplitCsv(map)) {
                    var eq = pair.IndexOf('=');
                    if (eq <= 0) return "ERROR: map entry '" + pair + "' is not 'manifestName=targetScript'.";
                    mapPairs[pair.Substring(0, eq).Trim()] = pair.Substring(eq + 1).Trim();
                }

                var sb = new StringBuilder();
                sb.AppendLine("# JS IMPORT  from=" + manifestPath + "  entries=" + entries.Count + "  dryRun=" + dryRun);

                var tag = "import-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                var written = new List<string>();
                var snapTag = autoSnapshot ? tag : null;
                var stagedSnapshots = new List<string>();
                var okCount = 0;
                var skipCount = 0;
                var failCount = 0;
                var edited = 0;

                foreach (var e in entries) {
                    if (wanted.Count > 0 && !wanted.Contains(e.Name)) continue;
                    var file = Path.Combine(folder, e.File);
                    if (!File.Exists(file)) { skipCount++; sb.AppendLine("SKIP " + e.Name + ": file '" + e.File + "' missing in " + folder); continue; }

                    string source;
                    try { source = File.ReadAllText(file); }
                    catch (Exception ex) { failCount++; sb.AppendLine("FAIL " + e.Name + ": cannot read '" + e.File + "': " + ex.Message); continue; }
                    var fileRev = JsScriptHelper.Rev(source);
                    var fileEdited = !string.Equals(fileRev, e.Rev, StringComparison.OrdinalIgnoreCase);
                    if (fileEdited) edited++;

                    // ---- landing resolution: map -> key -> unique name
                    var target = e.Name;
                    var how = "name";
                    string keyFound = null, nameFound = null;
                    string dupName = null;
                    if (!string.IsNullOrEmpty(e.Key)) {
                        var byKey = JsScriptHelper.FindScript(doc, e.Key);
                        if (byKey != null) keyFound = JsScriptHelper.NameOf(byKey);
                    }
                    var byName = JsScriptHelper.FindScript(doc, e.Name);
                    if (byName != null) {
                        nameFound = JsScriptHelper.NameOf(byName);
                        // a name carried by more than one script is not a safe landing target
                        var hits = 0;
                        foreach (var s in JsScriptHelper.AllScripts(doc))
                            if (string.Equals(JsScriptHelper.NameOf(s), e.Name, StringComparison.OrdinalIgnoreCase)) hits++;
                        if (hits > 1) dupName = hits + " scripts share this name";
                    }

                    string targetKey = null;
                    if (mapPairs.ContainsKey(e.Name)) { target = mapPairs[e.Name]; how = "map"; }
                    else if (keyFound != null) { target = keyFound; how = "key"; }
                    else if (nameFound != null && dupName == null) { target = nameFound; how = "name"; }
                    else { target = e.Name; how = "name"; }

                    var existing = JsScriptHelper.FindScript(doc, target);
                    if (existing != null && dupName != null && how == "name") {
                        skipCount++;
                        sb.AppendLine("SKIP " + e.Name + ": ambiguous name (" + dupName + ") - use key or map=. Nothing written.");
                        continue;
                    }
                    if (existing == null && !createIfMissing) {
                        skipCount++;
                        sb.AppendLine("SKIP " + e.Name + " (-> '" + target + "'): no such script in the model."
                                    + " Add map=" + e.Name + "=<existingScript>, or pass createIfMissing=true.");
                        continue;
                    }
                    if (existing != null && !JsScriptHelper.IsJScript(existing)) {
                        skipCount++;
                        sb.AppendLine("SKIP " + e.Name + " (-> '" + target + "'): target is a "
                                    + JsScriptHelper.TypeName(existing) + " script, not a JScript.");
                        continue;
                    }

                    // ---- lint gate (same rules as js_write)
                    string lintWarnings;
                    var lintErrors = JsLintBridge.Run(doc, target, source, out lintWarnings);
                    if (lintErrors.Length > 0 && !force) {
                        skipCount++;
                        sb.AppendLine("SKIP " + e.Name + " (-> '" + target + "'): lint error(s), force=false.");
                        foreach (var line in lintErrors.Split('\n')) {
                            var t = line.TrimEnd('\r');
                            if (t.Length > 0) sb.AppendLine("      " + t);
                        }
                        continue;
                    }

                    var notes = new StringBuilder();
                    if (fileEdited) notes.Append(" (file edited after export: manifest rev=" + e.Rev + ", file rev=" + fileRev + ")");
                    if (lintWarnings.Length > 0) notes.Append(" (lint warning(s))");
                    notes.Append(" via " + how);

                    if (dryRun) {
                        sb.AppendLine("DRY  " + e.Name + " -> '" + target + "'" + notes + " chars=" + source.Length + " rev=" + fileRev
                                    + (existing == null ? " [would CREATE]" : " [would OVERWRITE]"));
                        continue;
                    }

                    // ---- real write: snapshot once per target, then the unified channel
                    if (existing != null && autoSnapshot && !stagedSnapshots.Contains(target)) {
                        var before = JsScriptHelper.ReadSource(existing) ?? "";
                        JsSnapshotStore.Capture(target, before, tag, true);
                        stagedSnapshots.Add(target);
                    }

                    ScriptContainer script;
                    string used;
                    bool created;
                    var err = JsScriptWrite.WriteScriptUnified(app, doc, target, source, createIfMissing, out script, out used, out created);
                    if (err != null) {
                        failCount++;
                        sb.AppendLine("FAIL " + e.Name + " -> '" + target + "': " + err);
                        JsDiagBuffer.Add("import", "error", target, err);
                        continue;
                    }
                    var readback = JsScriptHelper.ReadSource(script) ?? "";
                    var rev = JsScriptHelper.Rev(readback);
                    if (rev != fileRev) {
                        failCount++;
                        sb.AppendLine("FAIL " + e.Name + " -> '" + target + "': read-back rev " + rev + " != file rev " + fileRev);
                        continue;
                    }
                    okCount++;
                    written.Add(target);
                    sb.AppendLine((created ? "OK   created '" : "OK   wrote '") + target + "'" + notes
                                + " chars=" + source.Length + " rev=" + rev + " via " + used);
                    JsDiagBuffer.Add("import", "info", target, "imported " + source.Length + " chars, rev " + rev + " (tag " + tag + ")");
                }

                sb.AppendLine("import entries=" + okCount + " written, " + skipCount + " skipped, " + failCount + " failed, "
                            + edited + " file(s) edited after export  dryRun=" + dryRun);
                if (!dryRun && okCount > 0) {
                    sb.AppendLine(JsScriptWrite.Recompile(app, doc, null));
                    if (autoSnapshot) sb.AppendLine("Undo the whole import with: js_rollback(snapshot=\"" + tag + "\")");
                    if (verify) {
                        string note;
                        var failures = JsCompileVerify.RunBatch(app, written, out note);
                        sb.AppendLine("Compile verify: " + note);
                        foreach (var kv in failures) sb.AppendLine("  FAILED " + kv.Key + ": " + kv.Value);
                        if (failures.Count > 0 && autoSnapshot)
                            sb.AppendLine("Roll back the failed ones with: js_rollback(snapshot=\"" + tag + "\", targets=\""
                                        + string.Join(",", new List<string>(failures.Keys).ToArray()) + "\")");
                    } else {
                        sb.AppendLine("Compile NOT verified: JScript compiles at Reset / model open (plan §2.5)."
                                    + " Re-run with verify=true, or press Reset / reopen the model yourself.");
                    }
                }
                JsDiagBuffer.Add("import", failCount > 0 ? "warn" : "info", "",
                    (dryRun ? "dry-run " : "") + "entries=" + entries.Count + " ok=" + okCount + " skip=" + skipCount + " fail=" + failCount);
                return sb.ToString();
            });
        }
    }
}
