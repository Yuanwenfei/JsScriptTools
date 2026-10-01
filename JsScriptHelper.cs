using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Demo3D.Mcp;
using Demo3D.Visuals;

namespace Demo3D.Components {

    /// <summary>
    /// Shared kernel for the JScript tool set: lookup, reading, content fingerprint (Rev),
    /// running-state gate. Write/compile helpers live in JsScriptWrite.cs.
    /// API facts verified against Demo3D.Core.dll of Emulate3D 2026:
    ///  * Scripts live in Document.Resources.Scripts (ScriptContainerLibrary).
    ///  * ScriptContainer.Source is GET-ONLY; JScript writes go through disk .js + Load().
    /// </summary>
    internal static class JsScriptHelper {

        public const int MaxSourceLength = 2000000;
        public const int MaxPreviewLength = 4000;

        #region Lookup

        /// <summary>Returns the script library of a document, or null when unavailable.</summary>
        public static ScriptContainerLibrary Library(Document doc) {
            if (doc == null) return null;
            try {
                var res = doc.Resources;
                return res == null ? null : res.Scripts;
            } catch { return null; }
        }

        /// <summary>All script containers of a document (never null).</summary>
        public static List<ScriptContainer> AllScripts(Document doc) {
            var result = new List<ScriptContainer>();
            var lib = Library(doc);
            if (lib == null) return result;
            try {
                var all = lib.GetAll();
                if (all == null) return result;
                foreach (var s in all) if (s != null) result.Add(s);
            } catch { }
            return result;
        }

        /// <summary>Every JScript container of a document (never null).</summary>
        public static List<ScriptContainer> AllJScripts(Document doc) {
            var result = new List<ScriptContainer>();
            foreach (var s in AllScripts(doc)) if (IsJScript(s)) result.Add(s);
            return result;
        }

        /// <summary>Find a script container by name (case-insensitive), full name or key id.</summary>
        public static ScriptContainer FindScript(Document doc, string nameOrId) {
            if (doc == null || string.IsNullOrEmpty(nameOrId)) return null;
            var lib = Library(doc);
            if (lib != null) {
                try {
                    var direct = lib.Find(nameOrId);
                    if (direct != null) return direct;
                } catch { }
            }
            var all = AllScripts(doc);
            foreach (var s in all) if (string.Equals(Str(s, "Name"), nameOrId, StringComparison.OrdinalIgnoreCase)) return s;
            foreach (var s in all) if (string.Equals(Str(s, "FullName"), nameOrId, StringComparison.OrdinalIgnoreCase)) return s;
            foreach (var s in all) if (string.Equals(Str(s, "Key"), nameOrId, StringComparison.OrdinalIgnoreCase)) return s;
            return null;
        }

        /// <summary>Display name of a script container ("?" when unknown).</summary>
        public static string NameOf(ScriptContainer script) {
            return Str(script, "Name") ?? "?";
        }

        #endregion

        #region Reading

        /// <summary>Script type as text, e.g. "JScript".</summary>
        public static string TypeName(ScriptContainer script) {
            var t = Value(script, "PrimaryScriptType");
            return t == null ? "?" : Convert.ToString(t, CultureInfo.InvariantCulture);
        }

        /// <summary>True when the container reports itself as a JScript.</summary>
        public static bool IsJScript(ScriptContainer script) {
            return string.Equals(TypeName(script), "JScript", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Full source text of a script, using Source first and the primary native slot as
        /// fallback. Concatenating every slot is wrong for a CSharp project (.cs + .csproj):
        /// writing that back injects the project file into the source (historical defect #1).
        /// </summary>
        public static string ReadSource(ScriptContainer script) {
            if (script == null) return null;
            string src = null;
            try { src = script.Source; } catch { }
            if (!string.IsNullOrEmpty(src)) return src;

            var keys = NativeKeys(script);
            var key = ResolveKey(script, keys);
            if (string.IsNullOrEmpty(key)) return src;
            var bytes = NativeBytes(script, key);
            if (bytes == null) return src;
            var text = Decode(bytes);
            return text.Length == 0 ? src : text;
        }

        public static List<string> NativeKeys(ScriptContainer script) {
            var keys = new List<string>();
            if (script == null) return keys;
            try {
                var k = script.GetNativeSourcesKeys();
                if (k == null) return keys;
                foreach (var x in k) keys.Add(x);
            } catch { }
            return keys;
        }

        public static byte[] NativeBytes(ScriptContainer script, string key) {
            try { return script.GetNativeSources(key); } catch { return null; }
        }

        public static string Decode(byte[] data) {
            if (data == null || data.Length == 0) return "";
            try { return new UTF8Encoding(false).GetString(data); }
            catch { return Encoding.Default.GetString(data); }
        }

        /// <summary>Disk path when the script was loaded from disk, else null.</summary>
        public static string DiskPath(ScriptContainer script) {
            if (script == null) return null;
            try { return script.LoadedFromDiskPath; } catch { return null; }
        }

        static string ResolveKey(ScriptContainer script, List<string> keys) {
            foreach (var k in keys) {
                if (k == null) continue;
                var lk = k.ToLowerInvariant();
                if (lk.EndsWith(".js") || lk.EndsWith(".jscript")) return k;
            }
            if (keys.Count > 0) return keys[0];
            var name = Str(script, "Name");
            return string.IsNullOrEmpty(name) ? null : name + ".js";
        }

        /// <summary>Event-handler function names: <c>Xxx_OnEvent(</c> pattern (S5 naming convention).</summary>
        public static List<string> EventFunctions(string source) {
            var result = new List<string>();
            if (string.IsNullOrEmpty(source)) return result;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in Regex.Matches(source, @"function\s+([A-Za-z_][A-Za-z0-9_]*_On[A-Z][A-Za-z0-9_]*)\s*\(")) {
                if (seen.Add(m.Groups[1].Value)) result.Add(m.Groups[1].Value);
            }
            result.Sort(StringComparer.OrdinalIgnoreCase);
            return result;
        }

        #endregion

        #region Rev (content fingerprint)

        /// <summary>
        /// FNV-1a 64-bit fingerprint of the source as read back from the model (UTF-8, no BOM).
        /// Same convention as QLToolBox so both tool sets' manifests stay cross-comparable.
        /// Defined as the fingerprint of the in-model source, not of file bytes (S2/S7).
        /// </summary>
        public static string Rev(string source) {
            var bytes = new UTF8Encoding(false).GetBytes(source ?? "");
            ulong h = 14695981039346656037UL;
            for (var i = 0; i < bytes.Length; i++) {
                h ^= bytes[i];
                h *= 1099511628211UL;
            }
            return h.ToString("x16");
        }

        #endregion

        #region Running-state gate

        /// <summary>
        /// Tri-state running probe. The authoritative member is <c>IBuilder.Running</c> (the
        /// QLToolBox gate refuses on exactly this one — verified 2026-09-30). Document members
        /// such as <c>ModelTime</c>/<c>RunState</c>/<c>IsRunning</c> do NOT exist in 19.2.0.74:
        /// probing only those silently answered "unknown" and left the gate inactive, which is
        /// worse than having no gate because the report still claimed protection.
        /// true = running/paused, false = stopped, null = unknown (caller must say so).
        /// </summary>
        public static bool? IsModelRunning(IBuilder app, Document doc, out string detail) {
            var time = "";
            var t = Value(doc, "Time");
            if (t != null) time = " modelTime=" + Convert.ToString(t, CultureInfo.InvariantCulture);

            var v = Value(app, "Running");
            if (v is bool) {
                detail = "app.Running=" + ((bool)v ? "True" : "False") + time;
                return (bool)v;
            }

            foreach (var member in new[] { "IsRunning", "Running", "IsSimulationRunning" }) {
                var b = Value(doc, member);
                if (b is bool) {
                    detail = "document." + member + "=" + ((bool)b ? "True" : "False") + time;
                    return (bool)b;
                }
            }

            var state = Value(doc, "RunState");
            if (state != null) {
                var s = Convert.ToString(state, CultureInfo.InvariantCulture);
                if (s.Equals("Running", StringComparison.OrdinalIgnoreCase) || s.Equals("Paused", StringComparison.OrdinalIgnoreCase)) {
                    detail = "runState=" + s + time;
                    return true;
                }
                if (s.Equals("Stopped", StringComparison.OrdinalIgnoreCase) || s.Equals("Idle", StringComparison.OrdinalIgnoreCase)) {
                    detail = "runState=" + s + time;
                    return false;
                }
                detail = "runState=" + s + " (unrecognized)" + time;
                return null;
            }

            detail = "no running member found (app.Running absent)" + time;
            return null;
        }

        #endregion

        #region Small reflection helpers

        public static object Value(object target, string member) {
            if (target == null) return null;
            try {
                var pi = target.GetType().GetProperty(member, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (pi != null && pi.CanRead && pi.GetIndexParameters().Length == 0) return pi.GetValue(target, null);
                var fi = target.GetType().GetField(member, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (fi != null) return fi.GetValue(target);
            } catch { }
            return null;
        }

        public static string Str(object target, string member) {
            var v = Value(target, member);
            return v == null ? null : Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        public static string Invoke(Func<string> action) {
            try { return action(); }
            catch (TargetInvocationException ex) { return (ex.InnerException ?? ex).Message; }
            catch (Exception ex) { return ex.Message; }
        }

        public static string Truncate(string text, int max) {
            if (text == null) return "";
            if (max <= 0) max = MaxPreviewLength;
            return text.Length <= max ? text : text.Substring(0, max) + "...";
        }

        #endregion
    }
}
