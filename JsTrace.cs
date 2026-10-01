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

    /// <summary>
    /// Incident forensics (stage 4, task 2.15 — deliberately best effort).
    ///
    /// What the engine actually gives us (measured 2026-09-30, plan §2.5 / §3.5):
    ///   * compile failures are precise: `Compiling library 'X': expecting "RP", found '}'`
    ///     plus `line L:C:` in the inner-exception part -> script + exact position;
    ///   * runtime failures of the JScript engine do NOT carry a source position: e.g.
    ///     `Member 'ceil' not found on object 'Demo3D.Script.TypeLiteral'`.
    ///
    /// So a compile error is mapped exactly, and a runtime member error is mapped by searching the
    /// model's scripts for that member name and reporting the candidate call sites. Nothing is
    /// invented: when the tool cannot map something it says so and returns the original text.
    /// </summary>
    internal static class JsTraceKernel {

        internal class Parsed {
            public string Kind;        // compile-error | member-error | cast-error | unknown
            public string Library;     // script name when the text names one
            public string Message;
            public int Line;
            public int Column;
            public string Member;      // member-error: the missing member
            public string ObjectType;  // member-error: the object it was looked up on
            public string ExceptionType;
        }

        public static Parsed Parse(string text) {
            var p = new Parsed { Kind = "unknown" };
            if (string.IsNullOrEmpty(text)) return p;

            var ex = Regex.Match(text, @"([A-Za-z0-9_.]*(?:Exception|Error))\b");
            if (ex.Success) p.ExceptionType = ex.Groups[1].Value;

            var lib = Regex.Match(text, @"Compiling library '([^']+)'\s*:\s*([^\r\n]+)");
            if (lib.Success) {
                p.Kind = "compile-error";
                p.Library = lib.Groups[1].Value;
                p.Message = lib.Groups[2].Value.Trim();
            } else {
                var member = Regex.Match(text, @"Member '([^']+)' not found on object '([^']+)'");
                if (member.Success) {
                    p.Kind = "member-error";
                    p.Member = member.Groups[1].Value;
                    p.ObjectType = member.Groups[2].Value;
                    p.Message = "member '" + p.Member + "' not found on '" + p.ObjectType + "'";
                } else {
                    var cast = Regex.Match(text, @"Cannot cast '([^']*)'\s*\(([^)]*)\)\s*to\s*([A-Za-z0-9_.]+)");
                    if (cast.Success) {
                        p.Kind = "cast-error";
                        p.Message = "cannot cast " + cast.Groups[2].Value + " to " + cast.Groups[3].Value;
                    }
                }
            }

            // position: present for compile failures (inner-exception line)
            var pos = Regex.Match(text, @"line (\d+):(\d+)");
            if (pos.Success) {
                int v;
                if (int.TryParse(pos.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) p.Line = v;
                if (int.TryParse(pos.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) p.Column = v;
            }
            if (string.IsNullOrEmpty(p.Message)) {
                foreach (var raw in text.Replace("\r\n", "\n").Split('\n')) {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("---") || line.StartsWith("--[") || line.StartsWith("at ")) continue;
                    p.Message = line.Length > 200 ? line.Substring(0, 200) + "..." : line;
                    break;
                }
            }
            return p;
        }

        public static List<string> SplitLines(string src) {
            var lines = new List<string>();
            var text = (src ?? "").Replace("\r\n", "\n");
            foreach (var l in text.Split('\n')) lines.Add(l);
            return lines;
        }
    }

    public partial class JsScriptTools {

        [McpServerTool]
        [Description("Incident forensics: pass exception text (from get_log, an error dialog or a report) and get it mapped back to the JScript and source position. Compile failures are mapped exactly (the engine names the library and line:column); runtime member errors carry no position in this engine, so the tool searches the model for that member and lists candidate call sites. It cannot trap a crash by itself - pass the text in. Anything it cannot map is returned as-is, never guessed.")]
        public string JsTrace(
            [Description("The exception / stack text to analyse; empty = report the diagnostics of this session only")] string stack = "",
            [Description("Diagnostics to join: lastWrite | lastCompile | all")] string since = "all",
            [Description("Include the source excerpt around a mapped line")] bool source = true,
            [Description("Max candidate call sites for a member error")] int limit = 8) {
            return ExecuteMcpCommand(McpPermission.AllowScriptRead, app => {
                var doc = app.Document;
                var sb = new StringBuilder();

                var parsed = JsTraceKernel.Parse(stack);
                sb.AppendLine("# JS TRACE  " + (string.IsNullOrEmpty(stack) ? "(diagnostics only - no text passed)" : "(from the supplied text)"));
                sb.AppendLine("PARSED  kind=" + parsed.Kind
                            + (parsed.ExceptionType != null ? "  exception=" + parsed.ExceptionType : "")
                            + (parsed.Library != null ? "  library=" + parsed.Library : "")
                            + (parsed.Line > 0 ? "  line=" + parsed.Line + ":" + parsed.Column : "")
                            + (parsed.Member != null ? "  member=" + parsed.Member + " on " + parsed.ObjectType : ""));
                sb.AppendLine("MESSAGE " + (parsed.Message ?? "(none)"));

                // ---- mapping: compile errors point at a script + line
                if (parsed.Library != null) {
                    var s = JsScriptHelper.FindScript(doc, parsed.Library);
                    if (s == null) {
                        sb.AppendLine("MAP     script '" + parsed.Library + "' is not in the model (already purged? exported elsewhere?)");
                    } else {
                        var text = JsScriptHelper.ReadSource(s) ?? "";
                        var lines = JsTraceKernel.SplitLines(text);
                        sb.AppendLine("MAP     script '" + parsed.Library + "'  lines=" + lines.Count + "  rev=" + JsScriptHelper.Rev(text)
                                    + (parsed.Line > 0 && parsed.Line <= lines.Count
                                       ? "  -> line " + parsed.Line + ":" + parsed.Column
                                       : "  (the text carries no usable position)"));
                        if (source && parsed.Line > 0 && parsed.Line <= lines.Count) {
                            var from = Math.Max(1, parsed.Line - 3);
                            var to = Math.Min(lines.Count, parsed.Line + 3);
                            for (var i = from; i <= to; i++) {
                                sb.AppendLine((i == parsed.Line ? "  >> " : "     ")
                                            + i.ToString(CultureInfo.InvariantCulture).PadLeft(4) + ": " + lines[i - 1]);
                            }
                        }
                    }
                }

                // ---- mapping: member errors have no position, so search the model for the member
                if (parsed.Member != null) {
                    var needle = "." + parsed.Member;
                    var hits = 0;
                    sb.AppendLine("CANDIDATES for '" + needle + "' (the engine gives no line, so these are text matches):");
                    foreach (var s in JsScriptHelper.AllJScripts(doc)) {
                        var name = JsScriptHelper.NameOf(s);
                        var lines = JsTraceKernel.SplitLines(JsScriptHelper.ReadSource(s) ?? "");
                        for (var i = 0; i < lines.Count; i++) {
                            if (lines[i].IndexOf(needle, StringComparison.Ordinal) < 0) continue;
                            hits++;
                            sb.AppendLine("  " + name + ":" + (i + 1) + "  " + (lines[i].Length > 160 ? lines[i].Substring(0, 160) + "..." : lines[i].Trim()));
                            if (limit > 0 && hits >= limit) break;
                        }
                        if (limit > 0 && hits >= limit) break;
                    }
                    if (hits == 0) sb.AppendLine("  (no call site found in the model's JScripts - the code may be in a C# script or already changed)");
                    if (parsed.ObjectType != null && parsed.ObjectType.IndexOf("TypeLiteral", StringComparison.OrdinalIgnoreCase) >= 0) {
                        sb.AppendLine("HINT    'Demo3D.Script.TypeLiteral' means a *type/member lookup* failed, not a visual: this engine has no"
                                    + " lowercase JavaScript Math members (README §8.4: Math.ceil fails, Math.Sign works).");
                    }
                }

                var entries = JsDiagBuffer.Since(since, "", "warn", 0);
                sb.AppendLine("DIAG    since=" + since + " warn+error entries=" + entries.Count
                            + "  bufferMarks: " + JsDiagBuffer.DescribeMarks());
                foreach (var e in entries) {
                    sb.AppendLine("  " + e.Time.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " [" + e.Severity + "] " + e.Kind
                                + (e.Script == "" ? "" : " " + e.Script) + ": " + e.Message);
                }

                sb.AppendLine("HONESTY compile failures are mapped exactly (the engine names the library and line:column);"
                            + " runtime member errors carry NO position in this engine and are mapped by text search only."
                            + " If a mapping looks wrong, the original text above is authoritative.");
                return sb.ToString();
            });
        }
    }
}
