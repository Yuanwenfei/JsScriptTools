using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using Demo3D.Mcp;
using Demo3D.Visuals;

namespace Demo3D.Components {

    /// <summary>
    /// Write/compile half of the kernel. The JScript channel is disk .js + Load() only —
    /// SetNativeSources converts a JScript container into a CSharp project before throwing
    /// (historical defect #2), so JScript never falls through to the strategy chain.
    /// The strategy chain below serves CSharp/Project containers exclusively.
    /// </summary>
    internal static class JsScriptWrite {

        #region Writing

        /// <summary>
        /// Write new source text into a script container.
        /// Returns null on success, otherwise an error string; <paramref name="usedMethod"/>
        /// receives a short description of the mechanism that worked.
        /// </summary>
        public static string WriteSource(IBuilder app, Document doc, ScriptContainer script, string source,
                                         string preferredKey, out string usedMethod) {
            usedMethod = null;
            if (script == null) return "Script container is null.";
            if (source == null) source = "";

            var name = JsScriptHelper.NameOf(script);
            var old = JsScriptHelper.ReadSource(script);
            var attempts = new List<string>();

            // 0. JScript container without native slots: disk + Load, verify, never fall through.
            if (JsScriptHelper.IsJScript(script) && JsScriptHelper.NativeKeys(script).Count == 0) {
                string diskMethod;
                var diskErr = WriteViaDiskLoad(app, doc, script, source, out diskMethod);
                if (diskErr == null) {
                    var check = JsScriptHelper.ReadSource(script) ?? "";
                    if (check == source && JsScriptHelper.IsJScript(script)) { usedMethod = diskMethod; return null; }
                    attempts.Add(diskMethod + " but verify failed: type=" + JsScriptHelper.TypeName(script) + " length=" + check.Length);
                } else {
                    attempts.Add("disk+Load: " + diskErr);
                }
                return "Could not write the source of '" + name + "'. Attempts:\n  - " + string.Join("\n  - ", attempts.ToArray());
            }

            // 1. Public writable Source property (present in some builds).
            var err = TryPublicSetter(script, source);
            if (err == null) { usedMethod = "Source setter"; return null; }
            attempts.Add(err);

            // 2. Public/Non-public SetSource-style methods.
            err = TrySetMethod(script, source);
            if (err == null) { usedMethod = "SetSource method"; return null; }
            attempts.Add(err);

            // 3. Documented native-sources API: SetNativeSources(app, key, bytes).
            err = TryNativeSources(app, script, source, preferredKey, out var keyUsed);
            if (err == null) { usedMethod = "SetNativeSources[" + keyUsed + "]"; return null; }
            attempts.Add(err);

            // 4. Reflection: writable string property currently holding the source.
            err = TryBackingProperty(script, old, source);
            if (err == null) { usedMethod = "backing property"; return null; }
            attempts.Add(err);

            // 5. Reflection: string field currently holding the source.
            err = TryBackingField(script, old, source);
            if (err == null) { usedMethod = "backing field"; return null; }
            attempts.Add(err);

            // 6. Disk round trip for scripts loaded from disk.
            err = TryDiskReload(app, doc, script, source);
            if (err == null) { usedMethod = "disk reload"; return null; }
            attempts.Add(err);

            return "Could not write source of '" + name + "'. Attempts:\n  - " + string.Join("\n  - ", attempts.ToArray());
        }

        /// <summary>JScript-only channel: write a .js file into the user scripts folder and Load() it.</summary>
        static string WriteViaDiskLoad(IBuilder app, Document doc, ScriptContainer script, string source, out string usedMethod) {
            usedMethod = null;
            var name = JsScriptHelper.Str(script, "Name");
            if (string.IsNullOrEmpty(name)) return "script has no name";

            string dir;
            try { dir = SafeDir(app); } catch (Exception ex) { return "resolve folder: " + ex.Message; }

            string path;
            try {
                path = Path.Combine(dir, SafeFileName(name) + ".js");
                File.WriteAllText(path, source ?? "", new UTF8Encoding(false));
            } catch (Exception ex) { return "write " + dir + ": " + ex.Message; }

            try { script.Load(app, path, null, JsScriptHelper.Library(doc)); }
            catch (Exception ex) { return "Load: " + (ex.InnerException ?? ex).Message; }

            usedMethod = "disk .js + Load [" + path + "]";
            return null;
        }

        /// <summary>
        /// Single entry point for every write path (write loop, rollback, import).
        /// Finds or creates the script, writes through the type-safe channel, verifies
        /// read-back content and JScript type. Returns null on success.
        /// </summary>
        public static string WriteScriptUnified(IBuilder app, Document doc, string name, string source,
                                                bool createIfMissing, out ScriptContainer script, out string usedMethod,
                                                out bool created) {
            script = null;
            usedMethod = null;
            created = false;

            script = JsScriptHelper.FindScript(doc, name);
            if (script == null) {
                if (!createIfMissing) return "Script '" + name + "' not found.";
                string createDiag;
                try { script = CreateScript(app, doc, name, out createDiag); created = true; }
                catch (Exception ex) { return "ERROR creating script '" + name + "': " + ex.Message; }
                if (script == null) return "ERROR: could not create '" + name + "' as a JScript. Diagnostic: " + createDiag;
            } else if (!JsScriptHelper.IsJScript(script)) {
                return "ERROR: '" + name + "' is a " + JsScriptHelper.TypeName(script) + " script, not a JScript."
                     + " Writing JScript into it would break its compilation. Pick another name.";
            }

            var err = WriteSource(app, doc, script, source, "", out usedMethod);
            if (err != null) return err;

            var after = JsScriptHelper.ReadSource(script) ?? "";
            if (after != (source ?? "")) return "Verify failed: read-back content differs (length " + after.Length + ").";
            if (!JsScriptHelper.IsJScript(script)) return "Verify failed: type changed to " + JsScriptHelper.TypeName(script) + ".";
            return null;
        }

        /// <summary>Remove a script (rollback of a failed create), best effort.</summary>
        public static string TryRemove(Document doc, ScriptContainer script) {
            var lib = JsScriptHelper.Library(doc);
            if (lib == null || script == null) return "library unavailable";
            try { lib.Remove(script.Key); return null; }
            catch (Exception ex) { return ex.Message; }
        }

        static string TryPublicSetter(ScriptContainer script, string source) {
            var pi = script.GetType().GetProperty("Source", BindingFlags.Public | BindingFlags.Instance);
            if (pi == null || !pi.CanWrite) return "Source property has no public setter.";
            return JsScriptHelper.Invoke(() => { pi.SetValue(script, source, null); return null; });
        }

        static string TrySetMethod(ScriptContainer script, string source) {
            foreach (var mi in script.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)) {
                var n = mi.Name.ToLowerInvariant();
                var isSetter = n == "setsource" || n == "settext" || n == "setscriptsource" || n == "updatesource" || n == "writesource";
                if (!isSetter) continue;
                var ps = mi.GetParameters();
                if (ps.Length != 1 || ps[0].ParameterType != typeof(string)) continue;
                return JsScriptHelper.Invoke(() => { mi.Invoke(script, new object[] { source }); return null; });
            }
            return "No SetSource-style method found.";
        }

        static string TryNativeSources(IBuilder app, ScriptContainer script, string source, string preferredKey, out string keyUsed) {
            keyUsed = null;
            var keys = JsScriptHelper.NativeKeys(script);
            string key = !string.IsNullOrEmpty(preferredKey) ? preferredKey : ResolveKey(script, keys);
            if (string.IsNullOrEmpty(key)) return "No native source key available.";
            var isJsKey = key.EndsWith(".js", StringComparison.OrdinalIgnoreCase) || key.EndsWith(".jscript", StringComparison.OrdinalIgnoreCase);
            if (keys.Count > 0 && !keys.Contains(key) && string.IsNullOrEmpty(preferredKey) && !isJsKey) {
                return "Native source key '" + key + "' not present. Available: " + string.Join(", ", keys.ToArray());
            }
            var data = new UTF8Encoding(false).GetBytes(source);
            var err = JsScriptHelper.Invoke(() => { script.SetNativeSources(app, key, data); return null; });
            if (err == null) keyUsed = key;
            return err;
        }

        /// <summary>
        /// Choose the native source slot: prefer an existing .js/.jscript slot; otherwise the
        /// first slot (preserves the script type); only an empty container gets a new name.js.
        /// </summary>
        static string ResolveKey(ScriptContainer script, List<string> keys) {
            foreach (var k in keys) {
                if (k == null) continue;
                var lk = k.ToLowerInvariant();
                if (lk.EndsWith(".js") || lk.EndsWith(".jscript")) return k;
            }
            if (keys.Count > 0) return keys[0];
            var name = JsScriptHelper.Str(script, "Name");
            return string.IsNullOrEmpty(name) ? null : name + ".js";
        }

        /// <summary>
        /// String members that must never be treated as the source text: a brand new script
        /// (empty source) otherwise matches the first writable string member, which can be
        /// Name — renaming the script instead of filling it (historical defect #6).
        /// </summary>
        static readonly string[] NonSourceMembers = {
            "Name", "FullName", "Package", "LoadedFromDiskPath", "CreatedBy", "ModifiedBy", "SourceForToolTip"
        };

        static string TryBackingProperty(ScriptContainer script, string oldSource, string source) {
            foreach (var pi in script.GetType().GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)) {
                if (pi.PropertyType != typeof(string) || !pi.CanWrite || !pi.CanRead) continue;
                if (pi.GetIndexParameters().Length > 0) continue;
                string current;
                try { current = pi.GetValue(script, null) as string; } catch { continue; }
                if (!string.IsNullOrEmpty(oldSource) && !string.Equals(current, oldSource, StringComparison.Ordinal)) continue;
                if (Array.IndexOf(NonSourceMembers, pi.Name) >= 0) continue;
                if (string.IsNullOrEmpty(oldSource) && !string.IsNullOrEmpty(current)) continue;
                return JsScriptHelper.Invoke(() => { pi.SetValue(script, source, null); return null; });
            }
            return "No writable string property holds the current source.";
        }

        static string TryBackingField(ScriptContainer script, string oldSource, string source) {
            foreach (var fi in script.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)) {
                if (fi.FieldType != typeof(string) || fi.IsLiteral || fi.IsInitOnly) continue;
                string current;
                try { current = fi.GetValue(script) as string; } catch { continue; }
                if (!string.IsNullOrEmpty(oldSource) && !string.Equals(current, oldSource, StringComparison.Ordinal)) continue;
                if (Array.IndexOf(NonSourceMembers, fi.Name) >= 0) continue;
                if (string.IsNullOrEmpty(oldSource) && !string.IsNullOrEmpty(current)) continue;
                return JsScriptHelper.Invoke(() => { fi.SetValue(script, source); return null; });
            }
            return "No writable string field holds the current source.";
        }

        static string TryDiskReload(IBuilder app, Document doc, ScriptContainer script, string source) {
            string path = null;
            try { path = script.LoadedFromDiskPath; } catch { }
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return "Script was not loaded from an existing disk file.";
            var err = JsScriptHelper.Invoke(() => { File.WriteAllText(path, source, new UTF8Encoding(false)); return null; });
            if (err != null) return err;
            return JsScriptHelper.Invoke(() => { script.Load(app, path, null, JsScriptHelper.Library(doc)); return null; });
        }

        #endregion

        #region Compile / create / save

        /// <summary>Re-parse / recompile scripts and return a status line including compile errors.</summary>
        public static string Recompile(IBuilder app, Document doc, ScriptContainer script) {
            var sb = new StringBuilder();
            var lib = JsScriptHelper.Library(doc);
            if (lib != null) {
                var err = JsScriptHelper.Invoke(() => { lib.RunNewScriptsIfNecessary(doc); return null; });
                sb.Append(err == null ? "RunNewScriptsIfNecessary ok. " : "RunNewScriptsIfNecessary failed: " + err + " ");
            }
            var ext = UpdateScriptsExt(doc);
            sb.Append(ext == null ? "UpdateScripts ok. " : "UpdateScripts " + ext + " ");
            sb.Append(CollectErrors(script));
            return sb.ToString().Trim();
        }

        /// <summary>Returns null when the scripts were updated, otherwise a reason string.</summary>
        static string UpdateScriptsExt(Document doc) {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies()) {
                Type[] types;
                try { types = asm.GetTypes(); } catch { continue; }
                foreach (var t in types) {
                    if (t.Name != "DocumentUpdateScriptsExt") continue;
                    foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)) {
                        if (m.Name != "UpdateScripts" || m.GetParameters().Length != 1) continue;
                        try { m.Invoke(null, new object[] { doc }); return null; }
                        catch (Exception ex) { return "failed: " + (ex.InnerException ?? ex).Message; }
                    }
                }
            }
            return "not available in this build";
        }

        /// <summary>The container's own compile errors (never throws), capped at 20 lines.</summary>
        public static string CollectErrors(ScriptContainer script) {
            if (script == null) return "";
            IEnumerable errors = null;
            try { errors = script.Errors; } catch { }
            if (errors == null) return "";
            var sb = new StringBuilder();
            var n = 0;
            foreach (var e in errors) {
                if (e == null) continue;
                n++;
                if (n > 20) { sb.AppendLine("... more errors"); break; }
                sb.AppendLine(Convert.ToString(e, CultureInfo.InvariantCulture));
            }
            return n == 0 ? "" : "Compile errors (" + n + "):\n" + sb;
        }

        /// <summary>
        /// Create a brand new JScript container. lib.Create(name) has no type overload and
        /// already yields a JScript container (verified: createdType=JScript keys=[]), so no
        /// type correction is applied here (historical defect #3 stayed deleted).
        /// </summary>
        public static ScriptContainer CreateScript(IBuilder app, Document doc, string name, out string diagnostics) {
            var lib = JsScriptHelper.Library(doc);
            if (lib == null) throw new InvalidOperationException("Script library unavailable.");
            var created = lib.Create(name);
            if (created == null) { diagnostics = "lib.Create returned null."; return null; }

            diagnostics = "createdType=" + JsScriptHelper.TypeName(created)
                        + " keys=[" + string.Join("|", JsScriptHelper.NativeKeys(created).ToArray()) + "]";

            if (!JsScriptHelper.IsJScript(created)) {
                diagnostics += " (unexpected type)";
                try { lib.Remove(created.Key); diagnostics += "; removed=ok"; }
                catch (Exception ex) { diagnostics += "; removed=" + ex.Message; }
                return null;
            }
            return created;
        }

        /// <summary>Resolve the target file path for a save operation, with validation.</summary>
        public static string ResolvePath(IBuilder app, string filePath, string scriptName, out string error) {
            error = null;
            var path = (filePath ?? "").Trim();
            if (path.Length == 0) {
                var dir = SafeDir(app);
                path = Path.Combine(dir, SafeFileName(scriptName) + ".js");
            }
            if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0) { error = "Path contains invalid characters: " + path; return null; }
            if (!Path.IsPathRooted(path)) path = Path.Combine(SafeDir(app), path);
            var ext = Path.GetExtension(path);
            if (string.IsNullOrEmpty(ext)) path += ".js";
            return path;
        }

        static string SafeDir(IBuilder app) {
            string dir = null;
            try { dir = app.UserScriptsFolder; } catch { }
            if (string.IsNullOrEmpty(dir)) {
                try { dir = app.DocumentsDirectory; } catch { }
            }
            if (string.IsNullOrEmpty(dir)) dir = Path.GetTempPath();
            try { if (!Directory.Exists(dir)) Directory.CreateDirectory(dir); } catch { }
            return dir;
        }

        static string SafeFileName(string name) {
            var bad = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder();
            foreach (var c in name ?? "script") sb.Append(Array.IndexOf(bad, c) >= 0 ? '_' : c);
            return sb.Length == 0 ? "script" : sb.ToString();
        }

        #endregion
    }
}
