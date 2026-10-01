using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Demo3D.Mcp;
using Demo3D.Visuals;
using ModelContextProtocol.Server;

namespace Demo3D.Components {

    public partial class JsScriptTools {

        [McpServerTool]
        [Description("Write closed loop for a JScript: running gate -> static lint (error blocks unless force) -> auto snapshot -> write via disk .js + Load -> compile -> diagnostics -> rollback on failure. NOTE: JScript is compiled at Reset / model open, NOT at write time, so compilation is only verified when verify=true (presses Reset and scans the engine log) - the default path says so explicitly. Writing a script that lives only in the model makes it LoadedFromDisk=true (reported).")]
        public string JsWrite(
            [Description("Script name to create or modify")] string target,
            [Description("New JScript source text")] string source,
            [Description("Create the script when it does not exist yet")] bool createIfMissing = true,
            [Description("Recompile after writing")] bool compile = true,
            [Description("Only report what would happen, write nothing")] bool dryRun = false,
            [Description("Override a lint error block")] bool force = false,
            [Description("Take an auto snapshot before writing (strongly recommended)")] bool autoSnapshot = true,
            [Description("Real compile verification: press Reset and scan the engine log for a ScriptCompileException of this script, rolling back when it fails. Runs every OnReset handler in the scene, so it is opt-in")] bool verify = false) {
            if (string.IsNullOrEmpty(target)) return "ERROR: 'target' is required.";
            if (target.Length > 512) return "ERROR: 'target' is too long (max 512).";
            if (source == null) return "ERROR: 'source' is null.";
            if (source.Length > JsScriptHelper.MaxSourceLength) return "ERROR: source exceeds the " + JsScriptHelper.MaxSourceLength + " character limit.";

            return ExecuteMcpCommand(McpPermission.AllowScriptModification, app => {
                var doc = app.Document;
                var sb = new StringBuilder();

                // 1. Running gate.
                string runDetail;
                var running = JsScriptHelper.IsModelRunning(app, doc, out runDetail);
                if (running == true) return "REFUSED: the model is Running (" + runDetail + "). Stop the model first.";
                if (running == null) sb.AppendLine("NOTE: running-state unknown (" + runDetail + "), gate inactive.");

                // 2. Resolve target.
                var script = JsScriptHelper.FindScript(doc, target);
                var existed = script != null;
                string oldRev = null, oldDisk = null;
                if (existed) {
                    var oldSrc = JsScriptHelper.ReadSource(script);
                    oldRev = JsScriptHelper.Rev(oldSrc);
                    oldDisk = JsScriptHelper.Str(script, "LoadedFromDisk");
                }

                // 3. Static lint: the only pre-write guard there is (plan §2.5).
                string lintWarnings;
                var lintFindings = JsLintBridge.Run(doc, target, source, out lintWarnings);
                if (lintWarnings.Length > 0) sb.AppendLine(lintWarnings);
                if (lintFindings.Length > 0 && !force) {
                    JsDiagBuffer.Add("lint", "error", target, "blocked js_write (force=false)");
                    return "REFUSED: lint reported error-level findings (force=false to override).\n" + lintFindings;
                }

                // 4. Dry run.
                if (dryRun) {
                    sb.AppendLine("DRY-RUN: would " + (existed ? "overwrite" : "create") + " '" + target + "' (" + source.Length + " chars)."
                                + (existed ? " current rev=" + oldRev : "")
                                + (autoSnapshot && existed ? ", auto snapshot would be taken" : "")
                                + (compile ? ", then compile" : ""));
                    return sb.ToString();
                }

                // 5. Auto snapshot before any write (S3: purge happens on save — never skip).
                string snapTag = null;
                JsSnapshotStore.Snap snapEntry = null;
                if (autoSnapshot && existed) {
                    var oldSource = JsScriptHelper.ReadSource(script) ?? "";
                    snapTag = JsSnapshotStore.NewAutoTag();
                    snapEntry = JsSnapshotStore.Capture(target, oldSource, snapTag, true);
                    JsDiagBuffer.Add("snapshot", "info", target, "auto tag=" + snapTag + " chars=" + oldSource.Length + " rev=" + oldRev);
                }

                // 6. Write.
                ScriptContainer written;
                string used;
                bool created;
                var werr = JsScriptWrite.WriteScriptUnified(app, doc, target, source, createIfMissing, out written, out used, out created);
                if (werr != null) {
                    JsDiagBuffer.Add("write", "error", target, werr);
                    return "ERROR: " + werr;
                }

                var readback = JsScriptHelper.ReadSource(written) ?? "";
                var rev = JsScriptHelper.Rev(readback);
                // Mark BEFORE recording the entry: Since() is exclusive (entries strictly after the
                // mark), so marking first makes js_diag(since="lastWrite") show this write plus
                // everything it triggered, rollback included (plan D7).
                JsDiagBuffer.Mark("lastWrite");
                JsDiagBuffer.Add("write", "info", target,
                    (created ? "created" : "wrote") + " " + source.Length + " chars via " + used + ", read back rev " + rev);

                // 7. Compile.
                string compileLine = "compile skipped.";
                var hasErrors = false;
                if (compile) {
                    var sw = Stopwatch.StartNew();
                    compileLine = JsScriptWrite.Recompile(app, doc, written);
                    sw.Stop();
                    JsDiagBuffer.Mark("lastCompile");
                    var errs = JsScriptWrite.CollectErrors(written);
                    hasErrors = errs.Length > 0;
                    if (hasErrors) {
                        foreach (var line in errs.Split('\n')) {
                            var t = line.TrimEnd('\r');
                            if (t.Length > 0) JsDiagBuffer.Add("compile", "error", target, t);
                        }
                    }
                    compileLine = "CompileAll " + sw.ElapsedMilliseconds + " ms. " + compileLine;
                }

                // 8. Failure -> rollback inside this same call (R5: no window until save).
                if (hasErrors) {
                    RollbackAfterFailure(app, doc, written, created, snapEntry, snapTag, sb);
                    sb.AppendLine("Diagnosis: js_diag(since=\"lastWrite\")");
                    return sb.ToString();
                }

                // 9. Real compile verification (opt-in). The engine compiles JScript at Reset,
                //    not here, so without this step the compile result is simply unknown.
                var verifyLine = "";
                if (verify) {
                    string vnote;
                    var vfail = JsCompileVerify.Run(app, target, out vnote);
                    if (vfail != null) {
                        JsDiagBuffer.Add("compile", "error", target, vfail);
                        sb.AppendLine(vfail);
                        RollbackAfterFailure(app, doc, written, created, snapEntry, snapTag, sb);
                        sb.AppendLine("Model was Reset again after the rollback (best effort): "
                                    + (JsCompileVerify.ResetModel(app) ?? "ok"));
                        sb.AppendLine("Diagnosis: js_diag(since=\"lastWrite\")");
                        return sb.ToString();
                    }
                    JsDiagBuffer.Add("compile", "info", target, "verify ok: " + vnote);
                    verifyLine = "Compile verify: " + vnote;
                } else {
                    verifyLine = "Compile NOT verified: JScript is compiled at Reset / model open, never when it is "
                               + "written (plan §2.5), and the engine swallows the error into its log.\n"
                               + "  Re-run with verify=true for a real check (presses Reset, scans the engine log, "
                               + "rolls back on failure), or press Reset / reopen the model yourself.";
                }

                // 10. Report.
                sb.AppendLine((created ? "Created" : "Wrote") + " '" + target + "': " + source.Length + " chars via " + used + ".");
                sb.AppendLine("rev=" + rev
                            + (rev == JsScriptHelper.Rev(source) ? "" : " (input fingerprint differs — the engine normalized the source; see validation point B1)")
                            + (existed ? ", was " + oldRev : ""));
                if (existed && !string.Equals(oldDisk, "True", StringComparison.OrdinalIgnoreCase))
                    sb.AppendLine("NOTE: script was LoadedFromDisk=" + (oldDisk ?? "?") + " before this write; it is disk-ized now (S2 side effect, content verified intact).");
                sb.AppendLine(compileLine);
                sb.AppendLine("Verify: read back " + readback.Length + " chars, type=" + JsScriptHelper.TypeName(written) + ".");
                if (verifyLine.Length > 0) sb.AppendLine(verifyLine);
                return sb.ToString();
            });
        }

        /// <summary>
        /// Undo a failed write inside the same call (R5: no window until the model is saved).
        /// A script we created is removed; a script we overwrote is restored from its auto
        /// snapshot — including the Purge path, where the container no longer exists and has to
        /// be recreated (stage 1 acceptance A4).
        /// </summary>
        static void RollbackAfterFailure(IBuilder app, Document doc, ScriptContainer written, bool created,
                                         JsSnapshotStore.Snap snapEntry, string snapTag, StringBuilder sb) {
            if (created) {
                var rm = JsScriptWrite.TryRemove(doc, written);
                JsDiagBuffer.Add("rollback", "warn", "new script",
                    "failed write; created script " + (rm == null ? "removed" : "remove failed: " + rm));
                sb.AppendLine("WRITE FAILED -> created script removed" + (rm == null ? "" : " (remove failed: " + rm + ")") + ".");
            } else if (snapEntry != null) {
                ScriptContainer restored;
                var rerr = JsSnapshotStore.Restore(app, doc, snapEntry, out restored);
                JsScriptWrite.Recompile(app, doc, restored);
                if (rerr == null) {
                    JsDiagBuffer.Add("rollback", "warn", snapEntry.Script, "failed write; auto-rolled back to snapshot " + snapTag);
                    sb.AppendLine("WRITE FAILED -> auto-rollback to snapshot " + snapTag + " OK, rev restored=" + snapEntry.Rev + ".");
                } else {
                    JsDiagBuffer.Add("rollback", "error", snapEntry.Script, "auto-rollback FAILED: " + rerr);
                    sb.AppendLine("WRITE FAILED -> auto-rollback FAILED: " + rerr);
                }
            } else {
                sb.AppendLine("WRITE FAILED -> no snapshot was taken (autoSnapshot=false); script left as written.");
            }
            sb.AppendLine("Undo manually with: js_rollback(snapshot=\"" + (snapTag ?? "last") + "\")");
        }

        [McpServerTool]
        [Description("Lightweight write: no running gate, no lint, no snapshot, no auto-rollback. Use only right after js_snapshot or when the change is trivial. Returns the write mechanism and the read-back rev.")]
        public string JsSetSource(
            [Description("Script name to create or modify")] string target,
            [Description("New JScript source text")] string source,
            [Description("Create the script when it does not exist yet")] bool createIfMissing = true,
            [Description("Recompile after writing")] bool compile = false) {
            if (string.IsNullOrEmpty(target)) return "ERROR: 'target' is required.";
            if (source == null) return "ERROR: 'source' is null.";
            if (source.Length > JsScriptHelper.MaxSourceLength) return "ERROR: source exceeds the " + JsScriptHelper.MaxSourceLength + " character limit.";

            return ExecuteMcpCommand(McpPermission.AllowScriptModification, app => {
                var doc = app.Document;
                ScriptContainer script;
                string used;
                bool created;
                var err = JsScriptWrite.WriteScriptUnified(app, doc, target, source, createIfMissing, out script, out used, out created);
                if (err != null) {
                    JsDiagBuffer.Add("write", "error", target, err);
                    return "ERROR: " + err;
                }
                var readback = JsScriptHelper.ReadSource(script) ?? "";
                var rev = JsScriptHelper.Rev(readback);
                JsDiagBuffer.Mark("lastWrite");     // exclusive: mark first, then record (see js_write)
                JsDiagBuffer.Add("write", "info", target,
                    (created ? "created" : "wrote") + " " + source.Length + " chars via " + used + ", read back rev " + rev);
                var result = (created ? "Created" : "Wrote") + " '" + target + "' via " + used + ". read back rev " + rev + " (" + readback.Length + " chars).";
                if (compile) {
                    result += " " + JsScriptWrite.Recompile(app, doc, script);
                    result += " Compile NOT verified (JScript compiles at Reset / model open; use js_write(verify=true) for a real check).";
                } else {
                    result += " No compile requested; the model now holds unverified source.";
                }
                return result;
            });
        }
    }
}
