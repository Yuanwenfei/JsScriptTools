using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Text;
using Demo3D.Mcp;
using Demo3D.Visuals;
using ModelContextProtocol.Server;

namespace Demo3D.Components {

    /// <summary>
    /// MCP tool set for JScript (JS) scripts in an Emulate3D model.
    ///
    /// Stage 1 layout (development plan §2.1):
    ///  * This file: the original 9 tools, kept verbatim as the regression baseline.
    ///  * JsScriptHelper.cs: kernel — lookup, reading, Rev fingerprint, running gate.
    ///  * JsScriptWrite.cs: kernel — write channel, compile, create.
    ///  * JsDiag.cs: diagnostics ring buffer + js_diag.
    ///  * JsSnapshot.cs: snapshot repository + js_snapshot/js_diff/js_rollback.
    ///  * JsWrite.cs: write closed loop (js_write) + lightweight write (js_set_source).
    ///
    /// Deployment constraint (S6, corrected 2026-09-30 by measurement): the tool class is
    /// registered when the MODEL IS OPENED, not when Emulate3D starts. After pushing new
    /// sources: save the model (Ctrl+S), close and reopen the model, then refresh the MCP
    /// tool list on the client side. Restarting Emulate3D is NOT required. There is NO
    /// property-driven component channel (cancelled by user).
    ///
    /// REGISTRATION PITFALL: [McpServerToolType] MUST stay on the class. Without it the
    /// project still compiles with zero errors, but the MCP server never registers the
    /// class and every JS tool silently disappears from the tool list after a restart.
    /// The attribute only needs to appear on one part of this partial class.
    /// </summary>
    [McpServerToolType]
    [Description("JScript (JS) script tools: list JS scripts, read their source, create, modify, save to disk, reload and recompile them.")]
    public partial class JsScriptTools : McpToolBase {

        public JsScriptTools(McpPermissionHolder permissions) : base(permissions) { }

        // ---------------------------------------------------------------- read

        [McpServerTool]
        [Description("List every script in the model with its type and source length. JScript entries are the JS scripts.")]
        public string ListJsScripts(
            [Description("Optional name filter, empty for all scripts")] string nameFilter = "",
            [Description("Preview length in characters, 0 for no preview")] int previewLength = 120) {
            return ExecuteMcpCommand(McpPermission.AllowScriptRead, app => {
                var doc = app.Document;
                var scripts = JsScriptHelper.AllScripts(doc);
                if (scripts.Count == 0) return "No scripts in the model.";
                var sb = new StringBuilder();
                foreach (var s in scripts) {
                    var name = JsScriptHelper.Str(s, "Name") ?? "?";
                    var type = JsScriptHelper.TypeName(s);
                    if (!string.IsNullOrEmpty(nameFilter) &&
                        name.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) < 0 &&
                        type.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var src = JsScriptHelper.ReadSource(s) ?? "";
                    sb.AppendLine(name + " (" + type + ") length=" + src.Length +
                                  " nativeKeys=" + string.Join("|", JsScriptHelper.NativeKeys(s).ToArray()));
                    if (previewLength > 0) sb.AppendLine("    " + JsScriptHelper.Truncate(src, previewLength).Replace("\r\n", " ").Replace("\n", " "));
                }
                return sb.Length == 0 ? "No script matched '" + nameFilter + "'." : sb.ToString();
            });
        }

        [McpServerTool]
        [Description("Return the full source text of a JScript (JS) script in the model.")]
        public string GetJsScriptSource(
            [Description("Script name, e.g. CraneToolkitQS")] string scriptName) {
            var guard = GuardName(scriptName);
            if (guard != null) return guard;
            return ExecuteMcpCommand(McpPermission.AllowScriptRead, app => {
                var s = JsScriptHelper.FindScript(app.Document, scriptName);
                if (s == null) return NotFound(app.Document, scriptName);
                var src = JsScriptHelper.ReadSource(s);
                if (src == null) return "'" + scriptName + "' has no source in memory. Try InspectJsScript for details.";
                return "Script: " + scriptName + " (" + JsScriptHelper.TypeName(s) + ")\nLength: " + src.Length + "\n---- SOURCE ----\n" + src;
            });
        }

        [McpServerTool]
        [Description("Diagnostics for one script: type, native source keys, writability, disk path and compile errors. Use it when a write fails.")]
        public string InspectJsScript(
            [Description("Script name")] string scriptName) {
            var guard = GuardName(scriptName);
            if (guard != null) return guard;
            return ExecuteMcpCommand(McpPermission.AllowScriptRead, app => {
                var s = JsScriptHelper.FindScript(app.Document, scriptName);
                if (s == null) return NotFound(app.Document, scriptName);
                var sb = new StringBuilder();
                sb.AppendLine("Name: " + (JsScriptHelper.Str(s, "Name") ?? "?"));
                sb.AppendLine("FullName: " + (JsScriptHelper.Str(s, "FullName") ?? "-"));
                sb.AppendLine("Type: " + JsScriptHelper.TypeName(s));
                sb.AppendLine("SourceSetter: " + HasPublicSetter(s, "Source"));
                sb.AppendLine("IsNative: " + (JsScriptHelper.Str(s, "IsNative") ?? "-"));
                sb.AppendLine("HasDllSource: " + (JsScriptHelper.Str(s, "HasDllSource") ?? "-"));
                sb.AppendLine("IncludeInCompilation: " + (JsScriptHelper.Str(s, "IncludeInCompilation") ?? "-"));
                sb.AppendLine("LoadedFromDisk: " + (JsScriptHelper.Str(s, "LoadedFromDisk") ?? "-") + " path=" + (JsScriptHelper.Str(s, "LoadedFromDiskPath") ?? "-"));
                sb.AppendLine("NativeKeys: " + string.Join(", ", JsScriptHelper.NativeKeys(s).ToArray()));
                var src = JsScriptHelper.ReadSource(s);
                sb.AppendLine("SourceLength: " + (src == null ? 0 : src.Length));
                sb.AppendLine("Preview: " + JsScriptHelper.Truncate(src, 300).Replace("\r\n", " ").Replace("\n", " "));
                sb.Append(JsScriptWrite.CollectErrors(s));
                return sb.ToString();
            });
        }

        // ---------------------------------------------------------------- write

        [McpServerTool]
        [Description("Create or overwrite the source of a JScript (JS) script in the model, then recompile. Back up the model first.")]
        public string WriteJsScript(
            [Description("Script name to create or modify, e.g. CraneToolkitQS")] string scriptName,
            [Description("New JScript source text")] string source,
            [Description("Create the script when it does not exist yet")] bool createIfMissing = true,
            [Description("Native source key to overwrite, empty to auto-detect")] string sourceKey = "",
            [Description("Recompile after writing")] bool compile = true) {
            var guard = GuardName(scriptName);
            if (guard != null) return guard;
            if (source == null) return "ERROR: 'source' is null.";
            if (source.Length > JsScriptHelper.MaxSourceLength) return "ERROR: source exceeds the " + JsScriptHelper.MaxSourceLength + " character limit.";

            return ExecuteMcpCommand(McpPermission.AllowScriptModification, app => {
                var doc = app.Document;
                var s = JsScriptHelper.FindScript(doc, scriptName);
                var created = false;
                var createDiag = "";
                if (s == null) {
                    if (!createIfMissing) return "Script '" + scriptName + "' not found and createIfMissing is false.";
                    try { s = JsScriptWrite.CreateScript(app, doc, scriptName, out createDiag); created = true; }
                    catch (Exception ex) { return "ERROR creating script '" + scriptName + "': " + ex.Message; }
                    if (s == null) return "ERROR: could not create '" + scriptName + "' as a JScript. Diagnostic: " + createDiag;
                }

                var typeNow = JsScriptHelper.TypeName(s);
                if (!created && !JsScriptHelper.IsJScript(s)) {
                    return "ERROR: script '" + scriptName + "' is a " + typeNow + " script, not a JScript."
                         + " Writing JScript into it would break its compilation."
                         + " Pick another name, or convert the script in the Emulate3D UI first."
                         + " NativeKeys: " + string.Join(", ", JsScriptHelper.NativeKeys(s).ToArray());
                }
                var oldLength = (JsScriptHelper.ReadSource(s) ?? "").Length;
                string used;
                var err = JsScriptWrite.WriteSource(app, doc, s, source, sourceKey, out used);
                if (err != null) return "ERROR: " + err;

                var sb = new StringBuilder();
                sb.AppendLine((created ? "Created" : "Updated") + " script '" + scriptName + "' via " + used + ". old length=" + oldLength + " new length=" + source.Length + ".");
                var finalType = JsScriptHelper.TypeName(s);
                sb.AppendLine("Type: " + finalType + "  NativeKeys: " + string.Join(", ", JsScriptHelper.NativeKeys(s).ToArray()));
                if (createDiag != "") sb.AppendLine("CreateDiagnostic: " + createDiag);
                if (!JsScriptHelper.IsJScript(s)) sb.AppendLine("WARNING: type is " + finalType + ", not JScript. JScript source will not compile in a " + finalType + " script.");
                if (compile) sb.AppendLine(JsScriptWrite.Recompile(app, doc, s));
                var after = JsScriptHelper.ReadSource(s) ?? "";
                sb.AppendLine("Verify: length after write=" + after.Length + (after == source ? ", content matches." : ", CONTENT DIFFERS - the change may not be live, check InspectJsScript."));
                return sb.ToString();
            });
        }

        [McpServerTool]
        [Description("Replace one text fragment inside a JScript (JS) script instead of rewriting the whole file.")]
        public string ReplaceInJsScript(
            [Description("Script name")] string scriptName,
            [Description("Exact text to find")] string oldText,
            [Description("Replacement text")] string newText,
            [Description("Expected number of occurrences, 0 to accept any")] int expectedCount = 1) {
            var guard = GuardName(scriptName);
            if (guard != null) return guard;
            if (string.IsNullOrEmpty(oldText)) return "ERROR: 'oldText' is empty.";

            return ExecuteMcpCommand(McpPermission.AllowScriptModification, app => {
                var doc = app.Document;
                var s = JsScriptHelper.FindScript(doc, scriptName);
                if (s == null) return NotFound(doc, scriptName);
                var fragmentGuard = GuardFragmentEdit(s, scriptName);
                if (fragmentGuard != null) return fragmentGuard;
                var src = JsScriptHelper.ReadSource(s);
                if (src == null) return "'" + scriptName + "' has no readable source.";
                var count = Count(src, oldText);
                if (count == 0) return "ERROR: fragment not found in '" + scriptName + "'.";
                if (expectedCount > 0 && count != expectedCount) return "ERROR: found " + count + " occurrences, expected " + expectedCount + ".";

                string used;
                var err = JsScriptWrite.WriteSource(app, doc, s, src.Replace(oldText, newText ?? ""), "", out used);
                if (err != null) return "ERROR: " + err;
                return "Replaced " + count + " occurrence(s) in '" + scriptName + "' via " + used + ". " + JsScriptWrite.Recompile(app, doc, s);
            });
        }

        [McpServerTool]
        [Description("Append text to the end of a JScript (JS) script.")]
        public string AppendToJsScript(
            [Description("Script name")] string scriptName,
            [Description("Text to append")] string text) {
            var guard = GuardName(scriptName);
            if (guard != null) return guard;

            return ExecuteMcpCommand(McpPermission.AllowScriptModification, app => {
                var doc = app.Document;
                var s = JsScriptHelper.FindScript(doc, scriptName);
                if (s == null) return NotFound(doc, scriptName);
                var fragmentGuard = GuardFragmentEdit(s, scriptName);
                if (fragmentGuard != null) return fragmentGuard;
                var src = JsScriptHelper.ReadSource(s) ?? "";
                string used;
                var err = JsScriptWrite.WriteSource(app, doc, s, src + (text ?? ""), "", out used);
                if (err != null) return "ERROR: " + err;
                return "Appended " + (text ?? "").Length + " chars to '" + scriptName + "' via " + used + ". " + JsScriptWrite.Recompile(app, doc, s);
            });
        }

        [McpServerTool]
        [Description("Re-run the script compile step after script sources were changed.")]
        public string CompileJsScripts() {
            return ExecuteMcpCommand(McpPermission.AllowScriptModification, app => {
                var doc = app.Document;
                var sb = new StringBuilder();
                sb.AppendLine(JsScriptWrite.Recompile(app, doc, null));
                foreach (var s in JsScriptHelper.AllScripts(doc)) {
                    var e = JsScriptWrite.CollectErrors(s);
                    if (e.Length > 0) sb.AppendLine((JsScriptHelper.Str(s, "Name") ?? "?") + ": " + e);
                }
                return sb.ToString();
            });
        }

        // ---------------------------------------------------------------- save / load

        [McpServerTool]
        [Description("Save a JScript (JS) script from the model to a .js file on disk. Defaults to the user scripts folder.")]
        public string SaveJsScriptToDisk(
            [Description("Script name")] string scriptName,
            [Description("Target file path, empty for <user scripts folder>/<name>.js")] string filePath = "",
            [Description("Allow overwriting an existing file")] bool overwrite = false) {
            var guard = GuardName(scriptName);
            if (guard != null) return guard;
            return ExecuteMcpCommand(McpPermission.AllowScriptRead, app => {
                var s = JsScriptHelper.FindScript(app.Document, scriptName);
                if (s == null) return NotFound(app.Document, scriptName);
                var src = JsScriptHelper.ReadSource(s);
                if (src == null) return "'" + scriptName + "' has no readable source.";
                string err;
                var path = JsScriptWrite.ResolvePath(app, filePath, scriptName, out err);
                if (path == null) return "ERROR: " + err;
                if (File.Exists(path) && !overwrite) return "ERROR: '" + path + "' exists. Pass overwrite=true to replace it.";
                err = Safe(() => File.WriteAllText(path, src, new UTF8Encoding(false)));
                if (err != null) return "ERROR writing '" + path + "': " + err;
                return "Saved '" + scriptName + "' (" + src.Length + " chars) to " + path;
            });
        }

        [McpServerTool]
        [Description("Load a .js file from disk into a model script, creating the script when needed, then recompile.")]
        public string LoadJsScriptFromDisk(
            [Description("Script name to create or overwrite")] string scriptName,
            [Description("Source .js file path on disk")] string filePath,
            [Description("Create the script when it does not exist yet")] bool createIfMissing = true) {
            var guard = GuardName(scriptName);
            if (guard != null) return guard;
            if (string.IsNullOrEmpty(filePath)) return "ERROR: 'filePath' is empty.";

            return ExecuteMcpCommand(McpPermission.AllowScriptModification, app => {
                string source = null;
                var err = Safe(() => { source = File.ReadAllText(filePath); });
                if (err != null) return "ERROR reading '" + filePath + "': " + err;
                if (source == null) return "ERROR: could not read '" + filePath + "'.";
                if (source.Length > JsScriptHelper.MaxSourceLength) return "ERROR: source exceeds the " + JsScriptHelper.MaxSourceLength + " character limit.";

                var doc = app.Document;
                var s = JsScriptHelper.FindScript(doc, scriptName);
                var created = false;
                var createDiag = "";
                if (s == null) {
                    if (!createIfMissing) return "Script '" + scriptName + "' not found and createIfMissing is false.";
                    try { s = JsScriptWrite.CreateScript(app, doc, scriptName, out createDiag); created = true; }
                    catch (Exception ex) { return "ERROR creating script '" + scriptName + "': " + ex.Message; }
                    if (s == null) return "ERROR: could not create '" + scriptName + "' as a JScript. Diagnostic: " + createDiag;
                }

                string used;
                err = JsScriptWrite.WriteSource(app, doc, s, source, "", out used);
                if (err != null) return "ERROR: " + err;
                return (created ? "Created" : "Updated") + " script '" + scriptName + "' from '" + filePath + "' (" + source.Length + " chars) via " + used + ". "
                     + (createDiag == "" ? "" : "CreateDiagnostic: " + createDiag + ". ")
                     + JsScriptWrite.Recompile(app, doc, s);
            });
        }

        // ---------------------------------------------------------------- helpers

        /// <summary>
        /// Guard for the fragment level edit tools (ReplaceInJsScript, AppendToJsScript).
        ///
        /// Those tools read one text, edit it and write it back into a single slot. A multi
        /// slot project such as a CSharp project (a .cs slot plus a .csproj slot) cannot be
        /// represented by one text, so rewriting it would inject the project file into the
        /// source file. That is exactly how a project gets corrupted, so refuse instead.
        /// </summary>
        private static string GuardFragmentEdit(ScriptContainer script, string scriptName) {
            var keys = JsScriptHelper.NativeKeys(script);
            if (keys.Count > 1) {
                return "ERROR: '" + scriptName + "' is a multi-file project with slots ("
                     + string.Join(", ", keys.ToArray()) + "). "
                     + "Fragment editing rewrites a single slot and would corrupt the project. "
                     + "Use modify_source_within_project for this script.";
            }
            return null;
        }

        private static string GuardName(string scriptName) {
            if (string.IsNullOrEmpty(scriptName)) return "ERROR: 'scriptName' is required.";
            if (scriptName.Length > 512) return "ERROR: 'scriptName' is too long (max 512).";
            return null;
        }

        private static string NotFound(Document doc, string scriptName) {
            var all = JsScriptHelper.AllScripts(doc);
            if (all.Count == 0) return "Script '" + scriptName + "' not found. The model has no scripts at all.";
            var names = new List<string>();
            foreach (var s in all) names.Add(JsScriptHelper.Str(s, "Name") ?? "?");
            return "Script '" + scriptName + "' not found. Available scripts (" + names.Count + "): " + string.Join(", ", names.ToArray());
        }

        private static bool HasPublicSetter(object target, string property) {
            if (target == null) return false;
            try {
                var pi = target.GetType().GetProperty(property, BindingFlags.Public | BindingFlags.Instance);
                return pi != null && pi.CanWrite;
            } catch { return false; }
        }

        private static int Count(string haystack, string needle) {
            if (string.IsNullOrEmpty(haystack) || string.IsNullOrEmpty(needle)) return 0;
            var count = 0;
            var i = 0;
            while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { count++; i += needle.Length; }
            return count;
        }

        private static string Safe(Action action) {
            try { action(); return null; }
            catch (Exception ex) { return ex.Message; }
        }
    }
}
