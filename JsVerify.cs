using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Demo3D.Mcp;
using Demo3D.Visuals;

namespace Demo3D.Components {

    /// <summary>
    /// Post-write compile verification (development plan §2.5, measured 2026-09-30).
    ///
    /// JScript is compiled at Reset / model open, never when the source is written, and the
    /// engine swallows the resulting <c>Demo3D.Script.ScriptCompileException</c> into its log
    /// while the MCP reset call still answers <c>Success</c> (so it cannot be caught in-process).
    /// Verification is therefore: note the log length, press Reset, then read what the engine
    /// appended to <c>%APPDATA%\Emulate3D\demo3dlog.txt</c> and look for a compile failure that
    /// names this library.
    ///
    /// Opt-in only: Reset runs every OnReset handler in the scene, which is a real side effect
    /// and must never be a silent default.
    /// </summary>
    internal static class JsCompileVerify {

        const string LogRelative = @"Emulate3D\demo3dlog.txt";
        const int PollStepMs = 250;
        const int PollTotalMs = 3000;
        const int MaxReadBytes = 262144;

        /// <summary>Full path of the Emulate3D log, or null when it is not where we expect it.</summary>
        public static string LogPath() {
            try {
                var dir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                if (string.IsNullOrEmpty(dir)) dir = Environment.GetEnvironmentVariable("APPDATA");
                if (string.IsNullOrEmpty(dir)) return null;
                var path = Path.Combine(dir, LogRelative);
                return File.Exists(path) ? path : null;
            } catch { return null; }
        }

        /// <summary>
        /// Reset the model and report a compile failure of <paramref name="scriptName"/>.
        /// Returns null when nothing blamed that script (see <paramref name="note"/> for what was
        /// checked and for failures blamed on other scripts); otherwise a one-line report.
        /// </summary>
        public static string Run(IBuilder app, string scriptName, out string note) {
            note = null;
            var path = LogPath();
            if (path == null) {
                note = "log file not found, expected %APPDATA%\\" + LogRelative + " - compile NOT verified";
                return null;
            }

            var before = Length(path);
            if (before < 0) {
                note = "log file not readable - compile NOT verified";
                return null;
            }

            var thrown = ResetModel(app);

            var waited = 0;
            string delta = null;
            while (waited < PollTotalMs) {
                Thread.Sleep(PollStepMs);
                waited += PollStepMs;
                delta = ReadFrom(path, before);
                if (delta != null && delta.IndexOf("ScriptCompileException", StringComparison.OrdinalIgnoreCase) >= 0) break;
            }

            var text = (delta ?? "") + "\n" + (thrown ?? "");
            var mine = FailureFor(text, scriptName);
            var others = OtherFailures(text, scriptName);

            if (others.Count > 0) {
                note = "other script(s) also failed to compile: " + string.Join(", ", others.ToArray());
            }

            if (mine != null) return mine;

            note = (note == null ? "" : note + " | ")
                 + "Reset clean, no ScriptCompileException for '" + scriptName + "' (checked " + waited + " ms)"
                 + " - caveat: this only covers what the engine actually compiles, and mounting a script on a visual is a manual UI step (README §8.2)";
            return null;
        }

        /// <summary>
        /// Press Reset. The engine's reset path touches the UI (it writes the error list grid), so
        /// calling <c>app.Reset()</c> straight from an MCP tool thread does not compile anything —
        /// it raises a cross-thread <c>InvalidOperationException</c> from the DevExpress grid
        /// (measured 2026-09-30: two silent false negatives). Marshal to the UI thread when the
        /// app reports InvokeRequired, exactly like the UI's own Reset button does.
        /// Returns the exception message when one was thrown, else null.
        /// </summary>
        public static string ResetModel(IBuilder app) {
            var required = JsScriptHelper.Value(app, "InvokeRequired");
            var marshal = required is bool && (bool)required;
            if (!marshal) return JsScriptHelper.Invoke(() => { app.Reset(); return null; });

            var mi = app.GetType().GetMethod("Invoke", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance,
                                             null, new[] { typeof(Delegate), typeof(object[]) }, null);
            if (mi == null) return "cannot marshal Reset to the UI thread (IBuilder.Invoke unavailable)";
            return JsScriptHelper.Invoke(() => {
                mi.Invoke(app, new object[] { (Action)(() => app.Reset()), null });
                return null;
            });
        }

        /// <summary>
        /// One Reset for a whole batch (import / bulk edit): returns the failures among
        /// <paramref name="names"/> and a note describing what was checked. Import writes many
        /// scripts, so a single reset covers them all (plan §4.3).
        /// </summary>
        public static System.Collections.Generic.Dictionary<string, string> RunBatch(
                IBuilder app, System.Collections.Generic.IList<string> names, out string note) {
            var failures = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            note = null;
            var path = LogPath();
            if (path == null) {
                note = "log file not found, expected %APPDATA%\\" + LogRelative + " - compile NOT verified";
                return failures;
            }
            var before = Length(path);
            if (before < 0) {
                note = "log file not readable - compile NOT verified";
                return failures;
            }

            var thrown = ResetModel(app);
            var waited = 0;
            string delta = null;
            while (waited < PollTotalMs) {
                Thread.Sleep(PollStepMs);
                waited += PollStepMs;
                delta = ReadFrom(path, before);
                if (delta != null && delta.IndexOf("ScriptCompileException", StringComparison.OrdinalIgnoreCase) >= 0) break;
            }
            Thread.Sleep(400);
            var settled = ReadFrom(path, before);
            if (settled != null && (delta == null || settled.Length > delta.Length)) delta = settled;

            var text = (delta ?? "") + "\n" + (thrown ?? "");
            foreach (var name in names) {
                var failure = FailureFor(text, name);
                if (failure != null) failures[name] = failure;
            }
            var others = OtherFailures(text, null);
            foreach (var name in names) others.Remove(name);

            note = "Reset done, checked " + waited + " ms, batch=" + names.Count + ", failures=" + failures.Count
                 + (others.Count > 0 ? ", other broken script(s) outside this batch: " + string.Join(", ", others.ToArray()) : "");
            return failures;
        }

        /// <summary>The compile failure of one library, or null when the text does not blame it.</summary>
        static string FailureFor(string text, string scriptName) {
            if (string.IsNullOrEmpty(text)) return null;
            // Keep the compiler's own wording (it can contain quotes, e.g. expecting "RP", found '}')
            // and look for the position next to THIS match, not anywhere in the log tail.
            foreach (Match m in Regex.Matches(text, @"Compiling library '([^']+)'\s*:\s*([^\r\n]+)")) {
                var library = m.Groups[1].Value;
                if (!library.Equals(scriptName, StringComparison.OrdinalIgnoreCase)) continue;
                var message = m.Groups[2].Value.Trim().TrimEnd('.');
                if (message.Length > 300) message = message.Substring(0, 300) + "...";
                var where = LocationNear(text, m.Index);
                return "COMPILE VERIFY FAILED (after Reset): Compiling library '" + library + "': " + message + where + ".";
            }
            return null;
        }

        /// <summary>
        /// " at line L:C" for the exception block around <paramref name="matchIndex"/>, or "".
        /// The engine writes the exception dump in an order where the position line can come
        /// before OR after the "Compiling library" line (measured both ways), so look on both
        /// sides and take the nearest occurrence.
        /// </summary>
        static string LocationNear(string text, int matchIndex) {
            var start = Math.Max(0, matchIndex - 600);
            var length = Math.Min(1200, text.Length - start);
            if (length <= 0) return "";
            var window = text.Substring(start, length);
            var bestDistance = int.MaxValue;
            string best = null;
            foreach (Match loc in Regex.Matches(window, @"line (\d+):(\d+):")) {
                var distance = Math.Abs((start + loc.Index) - matchIndex);
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                best = loc.Groups[1].Value + ":" + loc.Groups[2].Value;
            }
            return best == null ? "" : " at line " + best;
        }

        /// <summary>Names of other libraries that failed in the same reset (deduplicated).</summary>
        static System.Collections.Generic.List<string> OtherFailures(string text, string scriptName) {
            var list = new System.Collections.Generic.List<string>();
            if (string.IsNullOrEmpty(text)) return list;
            foreach (Match m in Regex.Matches(text, @"Compiling library '([^']+)'")) {
                var library = m.Groups[1].Value;
                if (library.Equals(scriptName, StringComparison.OrdinalIgnoreCase)) continue;
                if (!list.Contains(library)) list.Add(library);
            }
            return list;
        }

        static long Length(string path) {
            try { return new FileInfo(path).Length; } catch { return -1; }
        }

        /// <summary>Everything the engine appended since the log had <paramref name="offset"/> bytes.</summary>
        static string ReadFrom(string path, long offset) {
            try {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) {
                    if (offset > fs.Length) offset = 0;
                    var len = (int)Math.Min(fs.Length - offset, MaxReadBytes);
                    if (len <= 0) return "";
                    fs.Seek(offset, SeekOrigin.Begin);
                    var buffer = new byte[len];
                    var read = fs.Read(buffer, 0, len);
                    if (read <= 0) return "";
                    return Encoding.UTF8.GetString(buffer, 0, read);
                }
            } catch { return null; }
        }
    }
}
