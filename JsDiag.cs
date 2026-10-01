using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using Demo3D.Mcp;
using ModelContextProtocol.Server;

namespace Demo3D.Components {

    /// <summary>
    /// In-process ring buffer for diagnostics (parse/compile/lint/write/rollback/snapshot).
    /// The process has no log-window access, so errors are captured at the source instead
    /// of being scraped from logs (same design as QLToolBox §5).
    /// Caveat (QLP pitfall E): static state is isolated per assembly — after a recompile /
    /// restart the buffer starts empty.
    /// </summary>
    internal static class JsDiagBuffer {
        internal class Entry {
            public long Seq;
            public DateTime Time;
            public string Kind;     // parse | compile | lint | write | rollback | snapshot | info
            public string Severity; // info | warn | error
            public string Script;   // empty = document-wide
            public string Message;
        }

        const int Capacity = 500;
        static readonly List<Entry> Ring = new List<Entry>();
        static long NextSeq = 1;
        static readonly Dictionary<string, long> Marks = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        public static void Add(string kind, string severity, string script, string message) {
            try {
                Ring.Add(new Entry {
                    Seq = NextSeq++,
                    Time = DateTime.Now,
                    Kind = kind ?? "info",
                    Severity = severity ?? "info",
                    Script = script ?? "",
                    Message = message ?? ""
                });
                if (Ring.Count > Capacity) Ring.RemoveRange(0, Ring.Count - Capacity);
            } catch { }
        }

        /// <summary>
        /// Record a mark ("lastWrite", "lastCompile") at the current sequence. Since() returns
        /// entries with a strictly greater Seq, so call this BEFORE recording the entry you want the
        /// mark to include (measured off-by-one: marking after the write entry hid it, B5).
        /// </summary>
        public static void Mark(string name) {
            if (string.IsNullOrEmpty(name)) return;
            Marks[name] = NextSeq - 1;
        }

        /// <summary>True when a mark was set in this session (so callers can say "never happened").</summary>
        public static bool HasMark(string name) {
            return !string.IsNullOrEmpty(name) && Marks.ContainsKey(name);
        }

        /// <summary>Entries with Seq greater than the given mark (or all when the mark is unknown).</summary>
        public static List<Entry> Since(string mark, string kind, string severityMin, int limit) {
            var result = new List<Entry>();
            long threshold = -1;
            if (!string.IsNullOrEmpty(mark) && !mark.Equals("all", StringComparison.OrdinalIgnoreCase)) {
                if (!Marks.TryGetValue(mark, out threshold)) return result; // unknown mark: nothing to report
            }
            var minSev = SeverityRank(severityMin);
            for (var i = Ring.Count - 1; i >= 0; i--) {
                var e = Ring[i];
                if (e.Seq <= threshold) break;
                if (!string.IsNullOrEmpty(kind) && !e.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase)) continue;
                if (SeverityRank(e.Severity) < minSev) continue;
                result.Add(e);
                if (limit > 0 && result.Count >= limit) break;
            }
            result.Reverse();
            return result;
        }

        public static string DescribeMarks() {
            var sb = new StringBuilder();
            foreach (var kv in Marks) sb.Append(kv.Key + "=" + kv.Value + " ");
            return sb.ToString().Trim();
        }

        static int SeverityRank(string s) {
            if (string.Equals(s, "error", StringComparison.OrdinalIgnoreCase)) return 2;
            if (string.Equals(s, "warn", StringComparison.OrdinalIgnoreCase)) return 1;
            return 0;
        }
    }

    public partial class JsScriptTools {

        [McpServerTool]
        [Description("Structured diagnostics from the in-process buffer: parse/compile/lint/write/rollback/snapshot events. since=lastWrite shows only what the last write produced.")]
        public string JsDiag(
            [Description("Since mark: lastWrite | lastCompile | all")] string since = "all",
            [Description("Filter by kind: parse | compile | lint | write | rollback | snapshot | info, empty for all")] string kind = "",
            [Description("Minimum severity: info | warn | error")] string severityMin = "info",
            [Description("Max entries, 0 for all")] int limit = 0) {
            return ExecuteMcpCommand(McpPermission.AllowScriptRead, app => {
                var entries = JsDiagBuffer.Since(since, kind, severityMin, limit);
                var sb = new StringBuilder();
                // An unknown mark silently yields "0 entries", which reads like "nothing happened".
                // Say it out loud instead: the operation the mark belongs to has not run yet.
                var markKnown = string.IsNullOrEmpty(since) || since.Equals("all", StringComparison.OrdinalIgnoreCase)
                              || JsDiagBuffer.HasMark(since);
                sb.AppendLine("diag since=" + since + " kind=" + (kind == "" ? "all" : kind)
                            + " severityMin=" + severityMin + " entries=" + entries.Count
                            + " bufferMarks: " + JsDiagBuffer.DescribeMarks());
                if (!markKnown) sb.AppendLine("NOTE mark '" + since + "' is not set in this session yet, so there is nothing to show (0 entries is NOT a clean result).");
                foreach (var e in entries) {
                    sb.AppendLine(e.Time.ToString("HH:mm:ss", CultureInfo.InvariantCulture)
                                + " #" + e.Seq + " [" + e.Severity + "] " + e.Kind
                                + (e.Script == "" ? "" : " " + e.Script) + ": " + e.Message);
                }
                return sb.ToString();
            });
        }
    }
}
