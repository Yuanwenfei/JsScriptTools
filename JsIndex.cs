using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Demo3D.Mcp;
using Demo3D.Visuals;
using ModelContextProtocol.Server;

namespace Demo3D.Components {

    public partial class JsScriptTools {

        [McpServerTool]
        [Description("Whole-model script inventory: name, type, lines, chars, rev fingerprint, disk-ized state and path, event-handler prefixes. Reconcile against the official get_scripts. mountedOn is deliberately NOT reported: mounting is not reflectable (README §8.2), so the event prefixes are listed instead.")]
        public string JsIndex(
            [Description("Regex filter on the script name, empty for all")] string nameRegex = "",
            [Description("Max rows, 0 for all")] int limit = 0,
            [Description("Kind: jscript (default) | all (includes C# projects)")] string kind = "jscript",
            [Description("Include per-script event handler names (longer output)")] bool events = true) {
            return ExecuteMcpCommand(McpPermission.AllowScriptRead, app => {
                var doc = app.Document;
                var all = string.Equals(kind, "all", StringComparison.OrdinalIgnoreCase)
                    ? JsScriptHelper.AllScripts(doc)
                    : JsScriptHelper.AllJScripts(doc);

                Regex filter = null;
                if (!string.IsNullOrEmpty(nameRegex)) {
                    try { filter = new Regex(nameRegex, RegexOptions.IgnoreCase); }
                    catch (Exception ex) { return "ERROR: invalid nameRegex: " + ex.Message; }
                }

                int visuals;
                var scene = JsLintEngine.SceneNames(doc, out visuals);
                var sceneKnown = visuals > 0;

                var rows = new List<string>();
                var jscript = 0;
                var other = 0;
                var matched = 0;
                long chars = 0;
                var totalEvents = 0;
                var unmounted = 0;
                foreach (var s in all) {
                    var name = JsScriptHelper.NameOf(s);
                    var isJs = JsScriptHelper.IsJScript(s);
                    if (filter != null && !filter.IsMatch(name)) continue;
                    matched++;
                    if (limit > 0 && rows.Count >= limit) continue;

                    var src = JsScriptHelper.ReadSource(s) ?? "";
                    chars += src.Length;
                    if (isJs) jscript++; else other++;
                    var disk = JsScriptHelper.Str(s, "LoadedFromDisk");
                    var path = JsScriptHelper.DiskPath(s);
                    var onDisk = string.Equals(disk, "True", StringComparison.OrdinalIgnoreCase);
                    var line = new StringBuilder();
                    line.Append(name);
                    line.Append("  ").Append(JsScriptHelper.TypeName(s));
                    line.Append("  lines=").Append(CountLines(src));
                    line.Append("  chars=").Append(src.Length);
                    line.Append("  rev=").Append(JsScriptHelper.Rev(src));
                    line.Append("  disk=").Append(onDisk ? "yes" : "no");
                    if (onDisk && !string.IsNullOrEmpty(path)) line.Append("(").Append(path).Append(")");
                    // JScript compile errors are NOT readable in-process (they appear at Reset), so
                    // the column is explicitly n/a instead of a fake 0 (plan §2.5).
                    line.Append("  errors=").Append(isJs ? "n/a" : "-");

                    if (isJs && events) {
                        var fns = JsScriptHelper.EventFunctions(src);
                        totalEvents += fns.Count;
                        var prefixes = new List<string>();
                        foreach (var fn in fns) {
                            var cut = fn.IndexOf("_On", StringComparison.Ordinal);
                            var p = cut > 0 ? fn.Substring(0, cut) : fn;
                            if (!prefixes.Contains(p)) prefixes.Add(p);
                        }
                        foreach (var p in prefixes) if (sceneKnown && !scene.Contains(p)) unmounted++;
                        line.Append("  events=").Append(fns.Count);
                        if (prefixes.Count > 0) line.Append(" [").Append(string.Join(", ", prefixes.ToArray())).Append("]");
                    }
                    rows.Add(line.ToString());
                }

                var sb = new StringBuilder();
                sb.AppendLine("# JS INDEX  kind=" + (string.IsNullOrEmpty(kind) ? "jscript" : kind)
                            + "  scripts=" + matched + " (JScript " + jscript + " / other " + other + ")"
                            + "  chars=" + chars
                            + "  sceneVisuals=" + visuals
                            + "  events=" + totalEvents
                            + (unmounted > 0 ? "  prefixesNotInScene=" + unmounted : "")
                            + "  revAlgo=fnv1a64");
                foreach (var r in rows) sb.AppendLine(r);
                if (limit > 0 && matched > rows.Count) sb.AppendLine("... " + (matched - rows.Count) + " more row(s) hidden by limit=" + limit);
                sb.AppendLine("NOTE rev = FNV-1a of the source as read back from the model; errors=n/a means the"
                            + " engine reports JScript compile errors only at Reset / model open (use js_write(verify=true) or js_lint).");
                return sb.ToString();
            });
        }

        static int CountLines(string src) {
            if (string.IsNullOrEmpty(src)) return 0;
            var n = 1;
            foreach (var c in src) if (c == '\n') n++;
            return n;
        }

    }
}
