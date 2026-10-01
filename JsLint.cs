using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Demo3D.Mcp;
using Demo3D.Visuals;
using ModelContextProtocol.Server;

namespace Demo3D.Components {

    /// <summary>
    /// JScript static checks. Stage 1 shipped the syntax pass (promoted to P0 on 2026-09-30 because
    /// JScript is NOT compiled when it is written — plan §2.5); stage 2 (task 2.8) adds the three
    /// model-aware rules behind the same engine and rule table.
    ///
    /// Rules (code / default severity):
    ///   syntax-error                  error  delimiters, strings, block comments, regex literals
    ///   unresolved-visual-ref         error  a statement that starts with `Name.` where Name is
    ///                                        neither declared in the script, nor a namespace root,
    ///                                        nor a visual of the scene (candidates are listed)
    ///   event-prefix-without-visual   warn   `X_OnEvent` whose X is not a visual of the scene —
    ///                                        the script is probably not mounted (mounting is a
    ///                                        manual UI step, README §8.2)
    ///   unavailable-api               error  measured engine gaps (README §8.4: no Math.*)
    ///
    /// Measuring stance (QLP §8 / 0 误报优先): a rule never asserts what it cannot decide. Findings
    /// that depend on the scene degrade to warn when the scene cannot be enumerated, and the syntax
    /// pass is deliberately conservative (it understands comments, strings and regex literals so it
    /// cannot mistake their contents for code).
    /// </summary>
    internal static class JsLintEngine {

        internal class Finding {
            public string Code;
            public string Severity;   // "error" | "warn"
            public int Line;
            public int Column;
            public string Message;
        }

        class Delim {
            public char C;
            public int Line;
            public int Column;
        }

        /// <summary>Namespace roots that may legitimately start a statement (`Demo3D.X.Y(...)`).</summary>
        static readonly string[] NamespaceRoots = {
            "Demo3D", "System", "Microsoft", "DevExpress", "Newtonsoft", "Sim3D", "Emulate3D", "Math"
        };

        /// <summary>
        /// Bare names the engine itself provides to every script: the App/Doc auto properties
        /// (documented as `App`/`Doc`, and the model's scripts use `app.`/`doc.`/`document.` in
        /// lower case) plus the classic sender parameter. Without these, `doc.` / `app.` were
        /// reported as unknown (measured false positives, 2026-09-30).
        /// </summary>
        static readonly string[] EngineGlobals = { "app", "doc", "document", "App", "Doc", "Document", "sender" };

        /// <summary>
        /// Measured engine gaps. The match is deliberately narrow: this engine resolves the .NET
        /// PascalCase members of Math (`Math.Sign(loc.Z)` runs in four model scripts), and only the
        /// lowercase JavaScript spellings fail (`Math.ceil` -> "Member 'ceil' not found", README §8.4).
        /// The first entry is a regex, the second the hint to print.
        /// </summary>
        static readonly string[][] UnavailableApi = {
            new[] { @"(?<![\w$.])Math\.[a-z]\w*",
                    "lowercase JavaScript Math members do not exist in this engine (README §8.4: Math.ceil fails)."
                  + " Use the .NET PascalCase member (Math.Sign/Math.Round work) or plain arithmetic" }
        };

        // ---------------------------------------------------------------- syntax pass

        /// <summary>
        /// Structural (syntax) findings. <paramref name="code"/> is the source with the CONTENT of
        /// comments, strings and regex literals blanked out (same length, newlines kept) so the
        /// model-aware rules can never match text inside a literal.
        /// </summary>
        public static List<Finding> SyntaxPass(string source, out string code, out int lineCount) {
            var findings = new List<Finding>();
            var src = source ?? "";
            var buf = src.ToCharArray();
            var stack = new List<Delim>();
            var line = 1;
            var col = 1;
            var i = 0;
            var regexAllowed = true;

            while (i < src.Length) {
                var c = buf[i];

                if (c == '\n') { line++; col = 1; i++; continue; }
                if (c == '\r') { i++; continue; }

                // ---- line comment: blank it
                if (c == '/' && i + 1 < src.Length && buf[i + 1] == '/') {
                    while (i < src.Length && buf[i] != '\n') { buf[i] = ' '; i++; }
                    continue;
                }

                // ---- block comment: blank it
                if (c == '/' && i + 1 < src.Length && buf[i + 1] == '*') {
                    var sl = line; var sc = col;
                    var closed = false;
                    while (i < src.Length) {
                        if (buf[i] == '*' && i + 1 < src.Length && buf[i + 1] == '/') { buf[i] = ' '; buf[i + 1] = ' '; i += 2; col += 2; closed = true; break; }
                        if (buf[i] == '\n') { line++; col = 1; } else { buf[i] = ' '; col++; }
                        i++;
                    }
                    if (!closed) Add(findings, "syntax-error", "warn", sl, sc, "unterminated block comment (/* without */)");
                    continue;
                }

                // ---- string literal: keep the quotes, blank the content
                if (c == '"' || c == '\'') {
                    var quote = c;
                    var sl = line; var sc = col;
                    i++; col++;
                    var closed = false;
                    while (i < src.Length) {
                        var d = buf[i];
                        if (d == '\\') {
                            buf[i] = ' '; i++; col++;
                            if (i < src.Length) {
                                if (buf[i] == '\n') { line++; col = 1; } else { buf[i] = ' '; col++; }
                                i++;
                            }
                            continue;
                        }
                        if (d == quote) { i++; col++; closed = true; break; }
                        if (d == '\n') break;
                        buf[i] = ' ';
                        i++; col++;
                    }
                    if (!closed) Add(findings, "syntax-error", "warn", sl, sc, "unterminated string literal");
                    regexAllowed = false;
                    continue;
                }

                // ---- regex literal: blank it (only in expression position)
                if (c == '/' && regexAllowed) {
                    var sl = line; var sc = col;
                    var inClass = false;
                    var closed = false;
                    while (i < src.Length) {
                        var d = buf[i];
                        if (d == '\\') {
                            buf[i] = ' '; i++; col++;
                            if (i < src.Length) {
                                if (buf[i] == '\n') { line++; col = 1; } else { buf[i] = ' '; col++; }
                                i++;
                            }
                            continue;
                        }
                        if (d == '\n') break;
                        if (d == '[') inClass = true;
                        else if (d == ']') inClass = false;
                        else if (d == '/' && !inClass) { i++; col++; closed = true; break; }
                        buf[i] = ' ';
                        i++; col++;
                    }
                    if (!closed) Add(findings, "syntax-error", "warn", sl, sc, "unterminated regular expression literal");
                    regexAllowed = false;
                    continue;
                }

                // ---- delimiters
                if (c == '(' || c == '[' || c == '{') {
                    stack.Add(new Delim { C = c, Line = line, Column = col });
                    regexAllowed = true;
                    i++; col++;
                    continue;
                }
                if (c == ')' || c == ']' || c == '}') {
                    var want = c == ')' ? '(' : (c == ']' ? '[' : '{');
                    if (stack.Count == 0) {
                        Add(findings, "syntax-error", "error", line, col, "unmatched '" + c + "' (nothing is open here)");
                    } else {
                        var top = stack[stack.Count - 1];
                        if (top.C != want) {
                            Add(findings, "syntax-error", "error", line, col,
                                "expecting '" + CloseOf(top.C) + "' to close '" + top.C + "' opened at line "
                                + top.Line.ToString(CultureInfo.InvariantCulture) + ":" + top.Column.ToString(CultureInfo.InvariantCulture)
                                + ", found '" + c + "'");
                        } else {
                            stack.RemoveAt(stack.Count - 1);
                        }
                    }
                    regexAllowed = false;
                    i++; col++;
                    continue;
                }

                // ---- identifier: `return /re/` must not be read as division
                if (char.IsLetter(c) || c == '_' || c == '$') {
                    var start = i;
                    while (i < src.Length && (char.IsLetterOrDigit(buf[i]) || buf[i] == '_' || buf[i] == '$')) { i++; col++; }
                    var word = src.Substring(start, i - start);
                    regexAllowed = word == "return" || word == "case" || word == "typeof" || word == "in" || word == "of";
                    continue;
                }

                if (!char.IsWhiteSpace(c)) regexAllowed = !char.IsLetterOrDigit(c);
                i++; col++;
            }

            foreach (var d in stack) {
                Add(findings, "syntax-error", "error", d.Line, d.Column,
                    "unclosed '" + d.C + "' (expecting '" + CloseOf(d.C) + "' before the end of the file)");
            }

            lineCount = line;
            code = new string(buf);
            return findings;
        }

        static char CloseOf(char open) {
            return open == '(' ? ')' : (open == '[' ? ']' : '}');
        }

        static void Add(List<Finding> list, string rule, string severity, int line, int col, string message) {
            list.Add(new Finding { Code = rule, Severity = severity, Line = line, Column = col, Message = message });
        }

        // ---------------------------------------------------------------- rules

        /// <summary>The declared symbols of a script: function names, parameters, var names.</summary>
        static HashSet<string> DeclaredSymbols(string code) {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in Regex.Matches(code, @"\bfunction\s+([A-Za-z_$][\w$]*)")) set.Add(m.Groups[1].Value);
            foreach (Match m in Regex.Matches(code, @"\bvar\s+([A-Za-z_$][\w$]*)")) set.Add(m.Groups[1].Value);
            // parameter lists of function declarations
            foreach (Match fn in Regex.Matches(code, @"\bfunction\b[^(]*\(([^)]*)\)")) {
                var ps = fn.Groups[1].Value;
                foreach (Match p in Regex.Matches(ps, @"(?:^|,)\s*([A-Za-z_$][\w$]*)\s*(?::|,|$)")) set.Add(p.Groups[1].Value);
            }
            return set;
        }

        /// <summary>
        /// Visual names of the scene. Document.Visuals is already flat (measured by QLToolBox on the
        /// same build); Children is still walked so the result stays correct elsewhere. De-duplicated
        /// by Id, because the model carries same-named visuals on purpose.
        /// </summary>
        public static HashSet<string> SceneNames(Document doc, out int visualCount) {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            visualCount = 0;
            if (doc == null) return names;
            var seen = new HashSet<string>();
            try {
                foreach (var v in doc.Visuals) CollectVisual(v, seen, names, ref visualCount);
            } catch { }
            return names;
        }

        static void CollectVisual(Visual v, HashSet<string> seen, HashSet<string> names, ref int count) {
            if (v == null) return;
            string id;
            try { id = Convert.ToString(v.Id, CultureInfo.InvariantCulture); } catch { id = null; }
            if (!string.IsNullOrEmpty(id)) {
                if (!seen.Add(id)) return;
            }
            count++;
            try {
                var name = v.Name;
                if (!string.IsNullOrEmpty(name)) names.Add(name);
            } catch { }
            IEnumerable kids = null;
            try {
                var pi = v.GetType().GetProperty("Children", BindingFlags.Public | BindingFlags.Instance);
                if (pi != null) kids = pi.GetValue(v, null) as IEnumerable;
            } catch { }
            if (kids == null) return;
            foreach (var k in kids) CollectVisual(k as Visual, seen, names, ref count);
        }

        /// <summary>`Name.` at the start of a statement, where Name is unknown to script, model and scene.</summary>
        static void CheckUnresolvedRefs(Ctx c) {
            var code = c.Code;
            var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // statement start = line start (with indentation) or right after ; { }
            foreach (Match m in Regex.Matches(code, @"(?:^[ \t]*|[;{}][ \t]*)([A-Za-z_$][\w$]*)[ \t]*\.", RegexOptions.Multiline)) {
                try {
                    var name = m.Groups[1].Value;
                    // the two start-of-statement alternatives can both fire around a block brace
                    // (`} FC.X()` / `FC.X()`), so collapse identical reports
                    if (!reported.Add(name + "|" + LineOf(code, m.Index + m.Groups[1].Index)
                                            + ":" + ColumnOf(code, m.Index + m.Groups[1].Index))) continue;
                    if (c.Declared.Contains(name)) continue;
                    // All JScripts of a model compile into one assembly, so a name declared in ANY
                    // script is reachable here (measured: FC/RM/VM helpers are cross-script globals).
                    if (c.Model.Contains(name)) continue;
                    if (c.Scene.Contains(name)) continue;
                    var known = false;
                    foreach (var ns in NamespaceRoots) if (string.Equals(ns, name, StringComparison.OrdinalIgnoreCase)) { known = true; break; }
                    if (!known) foreach (var g in EngineGlobals) if (string.Equals(g, name, StringComparison.OrdinalIgnoreCase)) { known = true; break; }
                    if (known) continue;

                    var pos = m.Index + m.Groups[1].Index;
                    var line = LineOf(code, pos);
                    var col = ColumnOf(code, pos);
                    // WARN, never error: a bare `Name.` can also be a static call on a type the engine
                    // resolves for us (`Transfer.Dispatch(...)`), and the host of a script is not
                    // reflectable (README §8.2), so this cannot be decided statically (measured
                    // 2026-09-30: error level produced type false positives). The sound half of the
                    // same check is event-prefix-without-visual, because event binding IS name based.
                    if (!c.SceneKnown) {
                        Add(c.Findings, "unresolved-visual-ref", "warn", line, col,
                            "'" + name + "' is not declared in this script, and the scene could not be enumerated to check it");
                        continue;
                    }
                    Add(c.Findings, "unresolved-visual-ref", "warn", line, col,
                        "'" + name + "' is not declared in this or any other script of the model, is not a scene visual"
                        + " and is not a known namespace/global. It may be a type, a host custom property or a missing reference"
                        + Nearest(name, c.Scene));
                } catch (Exception ex) {
                    Add(c.Findings, "unresolved-visual-ref", "warn", 0, 0,
                        "could not check one candidate name: " + ex.GetType().Name + ": " + ex.Message);
                }
            }
        }

        /// <summary>`X_OnEvent` handlers whose X is not a scene visual: one aggregated warning.</summary>
        static void CheckEventPrefixes(Ctx c) {
            if (!c.SceneKnown) return;
            var code = c.Code;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var missing = new List<string>();
            var firstLine = 0;
            foreach (Match m in Regex.Matches(code, @"\bfunction\s+([A-Za-z_$][\w$]*)_On[A-Z][\w$]*\s*\(")) {
                var prefix = m.Groups[1].Value;
                if (c.Scene.Contains(prefix)) continue;
                if (seen.Add(prefix)) {
                    missing.Add(prefix);
                    if (firstLine == 0) firstLine = LineOf(code, m.Index);
                }
            }
            if (missing.Count == 0) return;
            var shown = missing.Count <= 6 ? string.Join(", ", missing.ToArray())
                                           : string.Join(", ", missing.GetRange(0, 6).ToArray()) + ", +" + (missing.Count - 6) + " more";
            Add(c.Findings, "event-prefix-without-visual", "warn", firstLine, 1,
                missing.Count + " event handler prefix(es) are not visuals of the scene: " + shown
                + " — the script is probably not mounted (mounting is a manual UI step, README §8.2)");
        }

        static void CheckUnavailableApi(Ctx c) {
            foreach (var entry in UnavailableApi) {
                foreach (Match m in Regex.Matches(c.Code, entry[0])) {
                    Add(c.Findings, "unavailable-api", "error", LineOf(c.Code, m.Index), ColumnOf(c.Code, m.Index),
                        "'" + m.Value + "' is not available here: " + entry[1]);
                }
            }
        }

        /// <summary>
        /// Every symbol a script of this model may reference without declaring it: symbols declared in
        /// any other script, plus type names made available by `import`, `new T(...)` and type
        /// annotations (`x : Demo3D.Visuals.Transfer` makes the simple name `Transfer` usable).
        /// Reason this exists: all JScripts compile into one assembly, so cross-script globals
        /// (FC/RM/VM helpers in this model) and engine types are legitimately unknown to a single
        /// script (measured false positives, 2026-09-30).
        /// </summary>
        public static HashSet<string> ModelSymbols(Document doc) {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (doc == null) return set;
            foreach (var s in JsScriptHelper.AllJScripts(doc)) {
                var src = JsScriptHelper.ReadSource(s) ?? "";
                foreach (var d in DeclaredSymbols(src)) set.Add(d);
                foreach (Match m in Regex.Matches(src, @":\s*([A-Za-z_$][\w$.]*)")) AddQualified(set, m.Groups[1].Value);
                foreach (Match m in Regex.Matches(src, @"\bnew\s+([A-Za-z_$][\w$.]*)")) AddQualified(set, m.Groups[1].Value);
                foreach (Match m in Regex.Matches(src, @"\bimport\s+([A-Za-z_$][\w$.]*)\s*;")) AddQualified(set, m.Groups[1].Value);
            }
            return set;
        }

        static void AddQualified(HashSet<string> set, string qualified) {
            if (string.IsNullOrEmpty(qualified)) return;
            foreach (var part in qualified.Split('.')) {
                var t = part.Trim();
                if (t.Length > 0) set.Add(t);
            }
        }

        // ---- rule table (code -> severity/help/check) so a new rule never changes a signature

        /// <summary>Everything a rule may consult about one script.</summary>
        internal class Ctx {
            public string Code;                    // source with comments/strings/regex content blanked
            public HashSet<string> Declared;       // symbols declared in this script
            public HashSet<string> Model;          // symbols/types known anywhere in the model's JScripts
            public HashSet<string> Scene;          // visual names of the scene
            public bool SceneKnown;                // false when the scene could not be enumerated
            public List<Finding> Findings = new List<Finding>();
        }

        internal class Rule {
            public string Code;
            public string Severity;
            public string Help;
            public Action<Ctx> Check;
        }

        static readonly List<Rule> Table = new List<Rule> {
            new Rule { Code = "unresolved-visual-ref", Severity = "warn",
                       Help = "statement starts with an unknown `Name.` (warn: may be a type or a host custom property)", Check = CheckUnresolvedRefs },
            new Rule { Code = "event-prefix-without-visual", Severity = "warn",
                       Help = "event handler prefix is not a scene visual", Check = CheckEventPrefixes },
            new Rule { Code = "unavailable-api", Severity = "error",
                       Help = "engine API gap (README §8.4)", Check = CheckUnavailableApi }
        };

        public static string KnownRules() {
            var names = new List<string> { "syntax-error" };
            foreach (var r in Table) names.Add(r.Code);
            return string.Join(", ", names.ToArray());
        }

        /// <summary>
        /// Full analysis of one script. <paramref name="ruleCsv"/> empty = all rules; unknown names
        /// return an error instead of silently analysing nothing.
        /// <paramref name="flags"/> receives the findings, severity and problem kind for callers
        /// that only need the summary line.
        /// </summary>
        public static List<Finding> Analyze(Document doc, string source, string ruleCsv, out int lineCount, out string error) {
            return Analyze(doc, source, ruleCsv, null, out lineCount, out error);
        }

        /// <summary>
        /// Same, with a caller-supplied model symbol set so a full-model lint run computes it once
        /// (pass null to let this call compute it).
        /// </summary>
        public static List<Finding> Analyze(Document doc, string source, string ruleCsv, HashSet<string> modelSymbols,
                                            out int lineCount, out string error) {
            error = null;
            if (!string.IsNullOrEmpty(ruleCsv) && !AnyKnownRule(ruleCsv)) {
                error = "No matching rules for '" + ruleCsv + "'. Known: " + KnownRules();
                lineCount = 0;
                return new List<Finding>();
            }

            var selected = new List<Rule>();
            foreach (var r in Table) {
                if (string.IsNullOrEmpty(ruleCsv) || HasRule(ruleCsv, r.Code)) selected.Add(r);
            }

            string code;
            var findings = SyntaxPass(source, out code, out lineCount);
            if (!string.IsNullOrEmpty(ruleCsv) && !HasRule(ruleCsv, "syntax-error")) {
                findings.RemoveAll(f => f.Code == "syntax-error");
            }
            if (selected.Count == 0) return findings;

            int visuals;
            var scene = SceneNames(doc, out visuals);
            var ctx = new Ctx {
                Code = code,
                Declared = DeclaredSymbols(code),
                Model = modelSymbols ?? ModelSymbols(doc),
                Scene = scene,
                SceneKnown = visuals > 0,
                Findings = findings
            };
            foreach (var r in selected) {
                try { r.Check(ctx); }
                catch (Exception ex) {
                    var frames = (ex.StackTrace ?? "").Split('\n');
                    var at = frames.Length > 0 ? frames[0].Trim() : "";
                    Add(findings, r.Code, "warn", 0, 0, "rule could not run: " + ex.GetType().Name + ": " + ex.Message
                        + (at.Length > 0 ? " [" + at + "]" : ""));
                }
            }
            findings.Sort((a, b) => a.Line != b.Line ? a.Line.CompareTo(b.Line) : a.Column.CompareTo(b.Column));
            return findings;
        }

        static bool HasRule(string csv, string code) {
            if (string.IsNullOrEmpty(csv)) return false;
            foreach (var raw in csv.Split(',')) {
                if (string.Equals(raw.Trim(), code, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        static bool AnyKnownRule(string csv) {
            if (HasRule(csv, "syntax-error")) return true;
            foreach (var r in Table) if (HasRule(csv, r.Code)) return true;
            return false;
        }

        // ---- formatting helpers

        public static int CountErrors(List<Finding> findings) {
            var n = 0;
            if (findings == null) return 0;
            foreach (var f in findings) if (f.Severity == "error") n++;
            return n;
        }

        public static int CountWarnings(List<Finding> findings) {
            var n = 0;
            if (findings == null) return 0;
            foreach (var f in findings) if (f.Severity != "error") n++;
            return n;
        }

        public static string Format(List<Finding> findings, string severity) {
            var sb = new StringBuilder();
            if (findings == null) return "";
            foreach (var f in findings) {
                if (!string.Equals(f.Severity, severity, StringComparison.OrdinalIgnoreCase)) continue;
                sb.AppendLine(f.Severity.ToUpperInvariant() + " [" + f.Code + "] line "
                            + f.Line.ToString(CultureInfo.InvariantCulture) + ":" + f.Column.ToString(CultureInfo.InvariantCulture)
                            + " " + f.Message);
            }
            return sb.ToString();
        }

        static int LineOf(string text, int index) {
            var line = 1;
            for (var i = 0; i < index && i < text.Length; i++) if (text[i] == '\n') line++;
            return line;
        }

        static int ColumnOf(string text, int index) {
            if (index > text.Length) index = text.Length;
            var col = 1;
            for (var i = index - 1; i >= 0; i--) {
                if (text[i] == '\n') break;
                col++;
            }
            return col;
        }

        /// <summary>" Did you mean 'a', 'b', 'c'?" for the closest names, or "" when nothing is close.</summary>
        static string Nearest(string name, HashSet<string> scene) {
            var best = new List<string>();
            var bestScore = int.MaxValue;
            foreach (var candidate in scene) {
                var d = Distance(name, candidate);
                if (d < bestScore) { bestScore = d; best.Clear(); best.Add(candidate); }
                else if (d == bestScore && best.Count < 3) best.Add(candidate);
            }
            var limit = Math.Max(2, name.Length / 3);
            if (best.Count == 0 || bestScore > limit) return "";
            return " Did you mean '" + string.Join("', '", best.ToArray()) + "'?";
        }

        static int Distance(string a, string b) {
            a = a ?? ""; b = b ?? "";
            var d = new int[a.Length + 1, b.Length + 1];
            for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
            for (var j = 0; j <= b.Length; j++) d[0, j] = j;
            for (var i = 1; i <= a.Length; i++) {
                for (var j = 1; j <= b.Length; j++) {
                    var cost = char.ToUpperInvariant(a[i - 1]) == char.ToUpperInvariant(b[j - 1]) ? 0 : 1;
                    d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                }
            }
            return d[a.Length, b.Length];
        }
    }

    /// <summary>
    /// The write loop's lint hook (development plan §2.4 step 3). Errors block js_write unless
    /// force=true; warnings are reported but never block.
    /// </summary>
    internal static class JsLintBridge {

        public static string Run(Document doc, string target, string source, out string warnings) {
            warnings = "";
            int lines;
            string error;
            var findings = JsLintEngine.Analyze(doc, source, "", JsLintEngine.ModelSymbols(doc), out lines, out error);
            if (error != null) return "lint: " + error;
            var errors = JsLintEngine.CountErrors(findings);
            var warns = JsLintEngine.CountWarnings(findings);
            if (warns > 0) {
                warnings = "lint: " + warns + " warning(s), not blocking\n" + JsLintEngine.Format(findings, "warn");
            }
            if (errors == 0) return "";
            return "lint: " + errors + " error(s) in " + lines + " line(s)\n" + JsLintEngine.Format(findings, "error");
        }
    }

    public partial class JsScriptTools {

        [McpServerTool]
        [Description("Static checks for JScript. Syntax pass (delimiters/strings/comments/regex) plus model-aware rules: unresolved-visual-ref (error), event-prefix-without-visual (warn, unmounted script), unavailable-api (error, e.g. Math.*). Error findings block js_write unless force=true. Empty target checks every JScript in the model - use it as the 0-false-positive baseline check.")]
        public string JsLint(
            [Description("Script name to check, empty to check every JScript in the model")] string target = "",
            [Description("Source text to check instead of the script's current in-model source")] string source = "",
            [Description("Rules to run: comma list of syntax-error | unresolved-visual-ref | event-prefix-without-visual | unavailable-api, empty for all")] string rules = "",
            [Description("Minimum severity to list: warn | error")] string severityMin = "warn") {
            return ExecuteMcpCommand(McpPermission.AllowScriptRead, app => {
                var doc = app.Document;
                var sb = new StringBuilder();
                var names = new List<string>();
                if (!string.IsNullOrEmpty(target)) {
                    names.Add(target.Trim());
                } else {
                    foreach (var s in JsScriptHelper.AllJScripts(doc)) names.Add(JsScriptHelper.NameOf(s));
                }
                if (names.Count == 0) return "No JScript scripts found.";

                int visuals;
                JsLintEngine.SceneNames(doc, out visuals);
                var modelSymbols = JsLintEngine.ModelSymbols(doc);   // once per run, not per script

                var totalErrors = 0;
                var totalWarns = 0;
                var clean = 0;
                foreach (var name in names) {
                    string text = source;
                    if (string.IsNullOrEmpty(text)) {
                        var s = JsScriptHelper.FindScript(doc, name);
                        if (s == null) { sb.AppendLine("SKIP " + name + " (not found)"); continue; }
                        text = JsScriptHelper.ReadSource(s) ?? "";
                    }
                    int lines;
                    string ruleError;
                    var findings = JsLintEngine.Analyze(doc, text, rules, modelSymbols, out lines, out ruleError);
                    if (ruleError != null) return ruleError;
                    var errors = JsLintEngine.CountErrors(findings);
                    var warns = JsLintEngine.CountWarnings(findings);
                    totalErrors += errors;
                    totalWarns += warns;
                    var listing = string.Equals(severityMin, "error", StringComparison.OrdinalIgnoreCase)
                        ? JsLintEngine.Format(findings, "error")
                        : JsLintEngine.Format(findings, "error") + JsLintEngine.Format(findings, "warn");
                    if (errors == 0 && warns == 0) {
                        clean++;
                        sb.AppendLine("OK   " + name + "  lines=" + lines + " chars=" + text.Length);
                    } else {
                        sb.AppendLine((errors > 0 ? "ERR  " : "WARN ") + name
                                    + "  lines=" + lines + " chars=" + text.Length
                                    + " errors=" + errors + " warns=" + warns);
                        sb.Append(listing);
                    }
                    if (!string.IsNullOrEmpty(target)) break;   // explicit target: single result
                }
                sb.AppendLine("lint scripts=" + names.Count + " clean=" + clean + " errors=" + totalErrors + " warns=" + totalWarns
                            + "  sceneVisuals=" + visuals
                            + "  rules=" + (string.IsNullOrEmpty(rules) ? "all" : rules)
                            + "  severityMin=" + (string.IsNullOrEmpty(severityMin) ? "warn" : severityMin));
                return sb.ToString();
            });
        }
    }
}
