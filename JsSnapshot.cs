using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using Demo3D.Mcp;
using Demo3D.Visuals;
using ModelContextProtocol.Server;

namespace Demo3D.Components {

    /// <summary>
    /// In-process source snapshot repository (stage 1 / P0).
    /// Snapshots store source TEXT only — the disk .js file is overwritten by every write
    /// (S1) and a script that fails compilation is purged on model save (S3), so snapshots
    /// are the only reliable undo. Static state is isolated per assembly (QLP pitfall E):
    /// snapshots do not survive an Emulate3D restart; use js_export for persistence.
    /// </summary>
    internal static class JsSnapshotStore {
        internal class Snap {
            public long Id;
            public string Tag;
            public DateTime Time;
            public string Script;
            public string Source;
            public string Rev;
            public bool Auto;
        }

        static readonly List<Snap> All = new List<Snap>();
        static long NextId = 1;

        public static string NewAutoTag() {
            return "auto-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        }

        /// <summary>Capture one entry. Skips scripts without readable source.</summary>
        public static Snap Capture(string scriptName, string source, string tag, bool auto) {
            if (source == null) return null;
            var s = new Snap {
                Id = NextId++,
                Tag = tag,
                Time = DateTime.Now,
                Script = scriptName,
                Source = source,
                Rev = JsScriptHelper.Rev(source),
                Auto = auto
            };
            All.Add(s);
            return s;
        }

        /// <summary>All entries of a tag ("last" = most recent tag that has entries).</summary>
        public static List<Snap> ByTag(string tag) {
            var result = new List<Snap>();
            if (string.IsNullOrEmpty(tag) || tag.Equals("last", StringComparison.OrdinalIgnoreCase)) {
                for (var i = All.Count - 1; i >= 0; i--) {
                    if (All[i].Auto) { tag = All[i].Tag; break; }
                }
                if (string.IsNullOrEmpty(tag) || tag.Equals("last", StringComparison.OrdinalIgnoreCase)) return result;
            }
            foreach (var s in All) if (s.Tag.Equals(tag, StringComparison.OrdinalIgnoreCase)) result.Add(s);
            return result;
        }

        public static List<string> Tags() {
            var seen = new List<string>();
            foreach (var s in All) {
                if (!seen.Contains(s.Tag)) seen.Add(s.Tag);
            }
            return seen;
        }

        /// <summary>Restore one snapshot entry into the model. Returns null on success.</summary>
        public static string Restore(IBuilder app, Document doc, Snap entry, out ScriptContainer script) {
            script = JsScriptHelper.FindScript(doc, entry.Script);
            var created = false;
            if (script == null) {
                // Purged (S3) or never existed: recreate the container, then fill it.
                string diag;
                try { script = JsScriptWrite.CreateScript(app, doc, entry.Script, out diag); created = true; }
                catch (Exception ex) { return "recreate failed: " + ex.Message; }
                if (script == null) return "recreate failed: " + diag;
            }
            string used;
            var err = JsScriptWrite.WriteSource(app, doc, script, entry.Source, "", out used);
            if (err != null) return err + (created ? " (script was recreated but the write failed)" : "");
            var back = JsScriptHelper.ReadSource(script) ?? "";
            if (back != entry.Source) return "verify failed: read-back differs (length " + back.Length + ").";
            return null;
        }
    }

    public partial class JsScriptTools {

        [McpServerTool]
        [Description("Source-level snapshots of JScript scripts. Auto-snapshots are taken before every write; use listOnly to inspect, js_diff to compare, js_rollback to restore.")]
        public string JsSnapshot(
            [Description("Comma-separated script names, empty for all JScript scripts")] string targets = "",
            [Description("Tag for this snapshot batch, empty for an auto timestamp")] string tag = "",
            [Description("Only list existing snapshots, capture nothing")] bool listOnly = false) {
            return ExecuteMcpCommand(McpPermission.AllowScriptRead, app => {
                var sb = new StringBuilder();
                if (listOnly) {
                    var tags = JsSnapshotStore.Tags();
                    sb.AppendLine("snapshots=" + tags.Count + (JsSnapshotStore.Tags().Count == 0 ? " (none yet)" : ""));
                    foreach (var t in tags) {
                        var entries = JsSnapshotStore.ByTag(t);
                        sb.AppendLine("  " + t + "  (" + entries.Count + " scripts, " + entries[0].Time.ToString("HH:mm:ss") + ")");
                    }
                    return sb.ToString();
                }

                tag = string.IsNullOrEmpty(tag) ? JsSnapshotStore.NewAutoTag() : tag.Trim();
                var doc = app.Document;
                var names = new List<string>();
                if (string.IsNullOrEmpty(targets)) {
                    foreach (var s in JsScriptHelper.AllJScripts(doc)) names.Add(JsScriptHelper.NameOf(s));
                } else {
                    foreach (var n in targets.Split(',')) {
                        var t = n.Trim();
                        if (t.Length > 0) names.Add(t);
                    }
                }

                var captured = 0;
                var skipped = new List<string>();
                foreach (var name in names) {
                    var script = JsScriptHelper.FindScript(doc, name);
                    if (script == null) { skipped.Add(name + " (not found)"); continue; }
                    if (!JsScriptHelper.IsJScript(script)) { skipped.Add(name + " (not a JScript)"); continue; }
                    var src = JsScriptHelper.ReadSource(script);
                    if (src == null) { skipped.Add(name + " (no readable source)"); continue; }
                    JsSnapshotStore.Capture(name, src, tag, true);
                    JsDiagBuffer.Add("snapshot", "info", name, "tag=" + tag + " chars=" + src.Length + " rev=" + JsScriptHelper.Rev(src));
                    captured++;
                    sb.AppendLine("OK   " + name + "  rev=" + JsScriptHelper.Rev(src) + "  chars=" + src.Length);
                }
                sb.AppendLine("snapshot tag=" + tag + " captured=" + captured + " skipped=" + skipped.Count);
                foreach (var s in skipped) sb.AppendLine("SKIP " + s);
                return sb.ToString();
            });
        }

        [McpServerTool]
        [Description("Line-level diff between a snapshot and the current in-model source (or between two snapshots).")]
        public string JsDiff(
            [Description("Snapshot tag or 'last'")] string from,
            [Description("Snapshot tag, or 'current' (default)")] string to = "current",
            [Description("Output format: text | json")] string format = "text") {
            if (string.IsNullOrEmpty(from)) return "ERROR: 'from' is required (snapshot tag or 'last').";
            return ExecuteMcpCommand(McpPermission.AllowScriptRead, app => {
                var doc = app.Document;
                var fromSnaps = JsSnapshotStore.ByTag(from);
                if (fromSnaps.Count == 0) return "No snapshots found for tag '" + from + "'. Use js_snapshot(listOnly=true).";
                var sb = new StringBuilder();
                var json = format.Equals("json", StringComparison.OrdinalIgnoreCase);
                if (json) sb.Append("{\"from\":\"" + from + "\",\"to\":\"" + to + "\",\"scripts\":[");
                var first = true;
                foreach (var snap in fromSnaps) {
                    string oldSrc = snap.Source, newSrc = null, newRev = null;
                    if (to.Equals("current", StringComparison.OrdinalIgnoreCase)) {
                        var cur = JsScriptHelper.FindScript(doc, snap.Script);
                        if (cur == null) { AppendDiffLine(sb, json, ref first, snap.Script, "missing-in-model", 0, 0, 0, null); continue; }
                        newSrc = JsScriptHelper.ReadSource(cur) ?? "";
                        newRev = JsScriptHelper.Rev(newSrc);
                    } else {
                        var toSnaps = JsSnapshotStore.ByTag(to);
                        JsSnapshotStore.Snap toSnap = null;
                        foreach (var t in toSnaps) if (t.Script.Equals(snap.Script, StringComparison.OrdinalIgnoreCase)) { toSnap = t; break; }
                        if (toSnap == null) { AppendDiffLine(sb, json, ref first, snap.Script, "missing-in-to", 0, 0, 0, null); continue; }
                        newSrc = toSnap.Source;
                        newRev = toSnap.Rev;
                    }
                    string hunks;
                    int changed, added, removed;
                    DiffLines(oldSrc, newSrc, out hunks, out changed, out added, out removed);
                    AppendDiffLine(sb, json, ref first, snap.Script,
                        changed == 0 ? "identical" : "changed", changed, added, removed, hunks,
                        oldRev: snap.Rev, newRev: newRev);
                }
                if (json) sb.Append("]}");
                return sb.ToString();
            });
        }

        [McpServerTool]
        [Description("Restore scripts from a snapshot. Purged scripts are recreated. Does not touch any enable flags (scripts have none). Undo of a failed import: js_rollback(snapshot=\"last\").")]
        public string JsRollback(
            [Description("Snapshot tag, or 'last' (default)")] string snapshot = "last",
            [Description("Comma-separated script names to restore, empty for all in the snapshot")] string targets = "") {
            return ExecuteMcpCommand(McpPermission.AllowScriptModification, app => {
                var entries = JsSnapshotStore.ByTag(snapshot);
                if (entries.Count == 0) return "No snapshots found for tag '" + snapshot + "'. Use js_snapshot(listOnly=true).";
                var doc = app.Document;
                var filter = new List<string>();
                if (!string.IsNullOrEmpty(targets)) {
                    foreach (var n in targets.Split(',')) { var t = n.Trim(); if (t.Length > 0) filter.Add(t); }
                }

                var sb = new StringBuilder();
                var restored = 0;
                var skipped = 0;
                foreach (var entry in entries) {
                    if (filter.Count > 0 && !filter.Contains(entry.Script)) { skipped++; continue; }
                    ScriptContainer script;
                    var err = JsSnapshotStore.Restore(app, doc, entry, out script);
                    if (err != null) {
                        sb.AppendLine("FAIL " + entry.Script + ": " + err);
                        JsDiagBuffer.Add("rollback", "error", entry.Script, err);
                        continue;
                    }
                    restored++;
                    sb.AppendLine("OK   " + entry.Script + "  restored rev=" + entry.Rev
                                + (JsScriptHelper.FindScript(doc, entry.Script) != null
                                   && JsScriptHelper.Rev(JsScriptHelper.ReadSource(JsScriptHelper.FindScript(doc, entry.Script))) == entry.Rev
                                   ? " (verified)" : " (VERIFY MISMATCH)"));
                    JsDiagBuffer.Add("rollback", "info", entry.Script, "restored from tag=" + entry.Tag + " rev=" + entry.Rev);
                }
                var compile = JsScriptWrite.Recompile(app, doc, null);
                JsDiagBuffer.Mark("lastCompile");
                sb.AppendLine("rollback snapshot=" + snapshot + " restored=" + restored + " skipped=" + skipped + " of " + entries.Count + " entries");
                sb.AppendLine(compile);
                return sb.ToString();
            });
        }

        static void AppendDiffLine(StringBuilder sb, bool json, ref bool first, string script,
                                   string status, int changed, int added, int removed, string hunks,
                                   string oldRev = null, string newRev = null) {
            if (json) {
                if (!first) sb.Append(",");
                first = false;
                sb.Append("{\"script\":\"" + script + "\",\"status\":\"" + status + "\",\"changed\":" + changed
                        + ",\"added\":" + added + ",\"removed\":" + removed
                        + (oldRev != null ? ",\"fromRev\":\"" + oldRev + "\"" : "")
                        + (newRev != null ? ",\"toRev\":\"" + newRev + "\"" : "")
                        + (hunks != null ? ",\"diff\":" + EscapeJson(hunks) : "") + "}");
            } else {
                sb.AppendLine(script + ": " + status
                            + (changed > 0 ? " changed=" + changed + " +" + added + "/-" + removed : "")
                            + (oldRev != null && newRev != null ? "  rev " + oldRev + " -> " + newRev : ""));
                if (!string.IsNullOrEmpty(hunks)) sb.Append(hunks);
            }
        }

        static string EscapeJson(string s) {
            return "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n") + "\"";
        }

        /// <summary>
        /// Line diff with common head/tail trimming. The middle is reported as one
        /// -old/+new hunk: counts are exact, content is correct, minimality is not
        /// attempted (scripts here are &lt;= ~1000 lines; a rewrite shows as one hunk).
        /// </summary>
        static void DiffLines(string oldSrc, string newSrc, out string hunks, out int changed, out int added, out int removed) {
            var a = SplitLines(oldSrc ?? "");
            var b = SplitLines(newSrc ?? "");
            var pre = 0;
            while (pre < a.Count && pre < b.Count && a[pre] == b[pre]) pre++;
            var post = 0;
            while (post < a.Count - pre && post < b.Count - pre && a[a.Count - 1 - post] == b[b.Count - 1 - post]) post++;
            var aMid = a.Count - pre - post;
            var bMid = b.Count - pre - post;
            changed = aMid + bMid;
            added = bMid;
            removed = aMid;
            if (changed == 0) { hunks = null; return; }
            var sb = new StringBuilder();
            sb.AppendLine("@@ -" + (pre + 1) + "," + aMid + " +" + (pre + 1) + "," + bMid + " @@");
            var cap = 200;
            for (var i = 0; i < aMid && cap > 0; i++, cap--) sb.AppendLine("- " + a[pre + i]);
            for (var i = 0; i < bMid && cap > 0; i++, cap--) sb.AppendLine("+ " + b[pre + i]);
            if (cap == 0) sb.AppendLine("... (hunk truncated)");
            hunks = sb.ToString();
        }

        static List<string> SplitLines(string s) {
            var lines = new List<string>();
            var start = 0;
            for (var i = 0; i < s.Length; i++) {
                if (s[i] == '\n') {
                    var line = s.Substring(start, i - start);
                    if (line.EndsWith("\r")) line = line.Substring(0, line.Length - 1);
                    lines.Add(line);
                    start = i + 1;
                }
            }
            if (start < s.Length) {
                var tail = s.Substring(start);
                if (tail.EndsWith("\r")) tail = tail.Substring(0, tail.Length - 1);
                lines.Add(tail);
            }
            return lines;
        }
    }
}
