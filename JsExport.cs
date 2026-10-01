using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using Demo3D.Mcp;
using Demo3D.Visuals;
using ModelContextProtocol.Server;

namespace Demo3D.Components {

    /// <summary>
    /// Batch export of JScript sources (stage 3, task 2.12). This is the only way to persist the
    /// in-process snapshot store: snapshots do not survive a model reopen (measured 2026-09-30,
    /// plan §2.5), so an export doubles as the durable form of "the model as it is now".
    ///
    /// Artifacts per run: one `&lt;name&gt;.js` per script with the exact source text, a machine
    /// manifest `js-export.json` and a human overview `js-export.md`. Every file is reconciled
    /// immediately after writing: the bytes are read back, decoded and re-fingerprinted, and the
    /// rev must equal the rev of the in-model source (QLP L4 practice).
    ///
    /// Exporting does NOT disk-ize the model scripts: it writes files, it never calls Load, so the
    /// S2 side effect (LoadedFromDisk=False -> True) is not triggered.
    /// </summary>
    internal static class JsExportKernel {

        /// <summary>Fallback root, deliberately distinct from QLToolBox's export folder (D9).</summary>
        public static string DefaultRoot() {
            var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrEmpty(docs)) docs = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(docs, "Emulate3D-JS-Export");
        }

        public static List<string> SplitCsv(string csv) {
            var list = new List<string>();
            if (string.IsNullOrEmpty(csv)) return list;
            foreach (var raw in csv.Split(',')) {
                var t = raw.Trim();
                if (t.Length > 0) list.Add(t);
            }
            return list;
        }

        public static int CountLines(string src) {
            if (string.IsNullOrEmpty(src)) return 0;
            var n = 1;
            foreach (var c in src) if (c == '\n') n++;
            return n;
        }

        public static string EscapeJson(string s) {
            if (s == null) return "";
            var sb = new StringBuilder(s.Length + 8);
            foreach (var c in s) {
                switch (c) {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.Append("\\u" + ((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        /// <summary>Write one file and reconcile it: byte count and re-fingerprinted rev must match.</summary>
        public static string WriteAndCheck(string path, string source, string expectedRev, out long bytes, out string writtenRev) {
            bytes = 0;
            writtenRev = null;
            try {
                var data = new UTF8Encoding(false).GetBytes(source ?? "");
                File.WriteAllBytes(path, data);
                bytes = data.Length;
            } catch (Exception ex) {
                return "write failed: " + ex.Message;
            }
            try {
                var back = File.ReadAllBytes(path);
                bytes = back.Length;
                writtenRev = JsScriptHelper.Rev(new UTF8Encoding(false).GetString(back));
            } catch (Exception ex) {
                return "read-back failed: " + ex.Message;
            }
            if (writtenRev != expectedRev) return "rev mismatch: model=" + expectedRev + " file=" + writtenRev;
            return null;
        }
    }

    public partial class JsScriptTools {

        [McpServerTool]
        [Description("Export JScript sources to disk: one <name>.js per script with the exact source text, plus js-export.json (machine manifest) and js-export.md (human overview). Every file is reconciled (bytes read back and re-fingerprinted, rev must match). This is also the durable form of the in-process snapshots, which do not survive a model reopen. Exporting does NOT disk-ize the model scripts (no Load, no S2 side effect).")]
        public string JsExport(
            [Description("Target folder, empty for <MyDocuments>\\Emulate3D-JS-Export\\export-<timestamp>")] string dir = "",
            [Description("Comma-separated script names, empty for every JScript in the model")] string targets = "",
            [Description("Only report what would be written, write nothing")] bool dryRun = false) {
            return ExecuteMcpCommand(McpPermission.AllowScriptRead, app => {
                var doc = app.Document;
                var wanted = JsExportKernel.SplitCsv(targets);

                var list = new List<ScriptContainer>();
                foreach (var s in JsScriptHelper.AllJScripts(doc)) {
                    if (wanted.Count > 0 && !wanted.Contains(JsScriptHelper.NameOf(s))) continue;
                    list.Add(s);
                }
                if (list.Count == 0) return "No JScript matched '" + targets + "'.";

                string root;
                try { root = string.IsNullOrEmpty(dir) ? JsExportKernel.DefaultRoot() : dir; }
                catch (Exception ex) { return "ERROR resolving the target folder: " + ex.Message; }
                var folder = string.IsNullOrEmpty(dir)
                    ? Path.Combine(root, "export-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture))
                    : root;

                var sb = new StringBuilder();
                sb.AppendLine("# JS EXPORT  dir=" + folder + "  scripts=" + list.Count + "  dryRun=" + dryRun);

                var manifest = new StringBuilder();
                var overview = new StringBuilder();
                overview.AppendLine("# JScript export overview");
                overview.AppendLine();
                overview.AppendLine("Exported " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                                  + " from " + list.Count + " script(s). rev = FNV-1a of the source as read back from the model.");
                overview.AppendLine();
                overview.AppendLine("| script | type | lines | chars | rev | on disk (model) |");
                overview.AppendLine("|---|---|---|---|---|---|");

                manifest.Append("{\"tool\":\"JsScriptTools\",\"kind\":\"jscript-export\",\"toolVersion\":\"stage3\",");
                manifest.Append("\"exportedAt\":\"" + JsExportKernel.EscapeJson(DateTime.Now.ToString("s", CultureInfo.InvariantCulture)) + "\",");
                manifest.Append("\"folder\":\"" + JsExportKernel.EscapeJson(folder) + "\",");
                manifest.Append("\"count\":" + list.Count + ",\"revAlgo\":\"fnv1a64\",\"scripts\":[");

                var ok = 0;
                var failed = 0;
                var first = true;
                var modelOnly = 0;
                foreach (var s in list) {
                    var name = JsScriptHelper.NameOf(s);
                    var src = JsScriptHelper.ReadSource(s) ?? "";
                    var rev = JsScriptHelper.Rev(src);
                    var lines = JsExportKernel.CountLines(src);
                    var disk = JsScriptHelper.Str(s, "LoadedFromDisk");
                    var path = JsScriptHelper.DiskPath(s);
                    var onDisk = string.Equals(disk, "True", StringComparison.OrdinalIgnoreCase);
                    if (!onDisk) modelOnly++;
                    var file = SafeFileName(name) + ".js";

                    long bytes = 0;
                    string fileRev = null;
                    if (dryRun) {
                        sb.AppendLine("DRY  " + name + "  lines=" + lines + " chars=" + src.Length
                                    + " rev=" + rev + " -> " + file);
                    } else {
                        try { Directory.CreateDirectory(folder); }
                        catch (Exception ex) { return "ERROR creating '" + folder + "': " + ex.Message; }
                        var full = Path.Combine(folder, file);
                        var err = JsExportKernel.WriteAndCheck(full, src, rev, out bytes, out fileRev);
                        if (err == null) {
                            ok++;
                            sb.AppendLine("OK   " + name + "  lines=" + lines + " chars=" + src.Length + " bytes=" + bytes
                                        + " rev=" + rev + " -> " + file);
                        } else {
                            failed++;
                            sb.AppendLine("FAIL " + name + ": " + err);
                        }
                    }

                    if (!first) manifest.Append(",");
                    first = false;
                    manifest.Append("{\"name\":\"" + JsExportKernel.EscapeJson(name) + "\"");
                    var key = JsScriptHelper.Str(s, "Key");
                    if (!string.IsNullOrEmpty(key)) manifest.Append(",\"key\":\"" + JsExportKernel.EscapeJson(key) + "\"");
                    manifest.Append(",\"type\":\"" + JsExportKernel.EscapeJson(JsScriptHelper.TypeName(s)) + "\"");
                    manifest.Append(",\"file\":\"" + JsExportKernel.EscapeJson(file) + "\"");
                    manifest.Append(",\"rev\":\"" + rev + "\"");
                    manifest.Append(",\"chars\":" + src.Length);
                    manifest.Append(",\"bytes\":" + (dryRun ? src.Length : bytes));
                    manifest.Append(",\"lines\":" + lines);
                    manifest.Append(",\"modelLoadedFromDisk\":" + (onDisk ? "true" : "false"));
                    if (onDisk && !string.IsNullOrEmpty(path))
                        manifest.Append(",\"modelDiskPath\":\"" + JsExportKernel.EscapeJson(path) + "\"");
                    manifest.Append("}");

                    overview.AppendLine("| " + name + " | " + JsScriptHelper.TypeName(s) + " | " + lines + " | " + src.Length
                                      + " | " + rev + " | " + (onDisk ? JsExportKernel.EscapeJson(path) : "no (model only)") + " |");
                }
                manifest.Append("]}");

                if (!dryRun) {
                    try {
                        File.WriteAllText(Path.Combine(folder, "js-export.json"), manifest.ToString(), new UTF8Encoding(false));
                        File.WriteAllText(Path.Combine(folder, "js-export.md"), overview.ToString(), new UTF8Encoding(false));
                    } catch (Exception ex) {
                        sb.AppendLine("WARN could not write the manifest files: " + ex.Message);
                    }
                    sb.AppendLine("MANIFEST " + Path.Combine(folder, "js-export.json"));
                    sb.AppendLine("OVERVIEW " + Path.Combine(folder, "js-export.md"));
                    sb.AppendLine("RECONCILE " + ok + "/" + list.Count + " file(s) verified byte for byte (rev of the file == rev of the model source)"
                                + (failed > 0 ? ", " + failed + " FAILED" : ""));
                    sb.AppendLine("NOTE " + modelOnly + " script(s) existed only in the model and are now on disk for the first time."
                                + " Exporting writes files only - it never calls Load, so an export does not disk-ize model scripts (S2 is not triggered).");
                } else {
                    sb.AppendLine("DRY-RUN: nothing written. Re-run with dryRun=false (default) to create "
                                + list.Count + " .js file(s) + js-export.json + js-export.md in " + folder);
                }
                JsDiagBuffer.Add("export", failed > 0 ? "warn" : "info", "",
                    (dryRun ? "dry-run " : "") + list.Count + " script(s) to " + folder + ", ok=" + ok + " failed=" + failed);
                return sb.ToString();
            });
        }

        /// <summary>File-name safe form of a script name (scripts may contain characters Windows rejects).</summary>
        static string SafeFileName(string name) {
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(name.Length);
            foreach (var c in name) sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
            return sb.ToString();
        }
    }
}
