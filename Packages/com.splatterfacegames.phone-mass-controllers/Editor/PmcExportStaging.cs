using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace Splatter.Pmc.Editor {

    /// <summary>
    /// Collect/copy/clean logic for <see cref="PmcBuildPreprocessor"/>.
    /// Deliberately free of UnityEngine/UnityEditor APIs so it can be driven from
    /// tests or a menu item without starting a player build.
    ///
    /// Every <c>PmcHost.ControllerDir</c> under <c>Assets/</c> is copied verbatim to
    /// <c>Assets/StreamingAssets/pmc/&lt;basename&gt;</c>, and the package's <c>Web/</c>
    /// SDK to <c>Assets/StreamingAssets/pmc/web</c>. StreamingAssets ships untouched,
    /// so served files keep their original bytes (Unity would otherwise import —
    /// and mangle — .html/.js, exactly the problem Godot's exporter has with
    /// .ctex remaps). At runtime the glue resolves <c>Assets/…</c> controller dirs
    /// to the staged location; see docs/exporting.md.
    /// </summary>
    public static class PmcExportStaging {
        /// <summary>Subdirectory of Assets/StreamingAssets that owns everything we stage.</summary>
        public const string StagedRoot = "pmc";
        /// <summary>Safety cap so a pathological scan can't blow up the player.</summary>
        public const int MaxFiles = 4096;

        static readonly Regex ServeDirectoryRe = new Regex(
            "ServeDirectory\\s*\\(\\s*@?\"[^\"]*\"\\s*,\\s*@?\"(Assets/[^\"]+)\"", RegexOptions.Compiled);
        static readonly Regex ControllerDirRe = new Regex(
            "[Cc]ontrollerDir\\s*=\\s*@?\"(Assets/[^\"]+)\"", RegexOptions.Compiled);

        /// <summary>A served directory found in a scene or script.</summary>
        public sealed class SourceDir {
            /// <summary>Project-relative path ("Assets/…").</summary>
            public string Rel;
            /// <summary>Where it was found (log/warning text).</summary>
            public string Reason;
        }

        /// <summary>One copy operation: <see cref="SourceAbs"/> → <c>Assets/StreamingAssets/&lt;DestRel&gt;</c>.</summary>
        public sealed class Entry {
            public string SourceAbs;
            public string DestRel;
            public string Reason;
        }

        public sealed class Plan {
            public readonly List<Entry> Entries = new List<Entry>();
            public readonly List<string> Warnings = new List<string>();
        }

        /// <summary>What a build staged — enough to put the project back afterwards.</summary>
        public sealed class Manifest {
            public readonly List<string> CreatedDirs = new List<string>();   // project-rel, parents first
            public readonly List<string> CreatedFiles = new List<string>();  // project-rel
            public readonly List<KeyValuePair<string, string>> Overwritten =
                new List<KeyValuePair<string, string>>();                    // project-rel dest -> backup abs
        }

        /// <summary>
        /// Map candidate ControllerDirs to staging entries. Non-Assets paths need
        /// nothing (absolute paths, or in-memory/http controllers); missing dirs and
        /// basename collisions are reported, not staged. <paramref name="webSdkAbs"/>
        /// is the package Web/ dir; it always stages as <c>pmc/web</c>.
        /// </summary>
        public static Plan CreatePlan(IEnumerable<SourceDir> candidates, string projectRoot, string webSdkAbs) {
            var plan = new Plan();
            var takenDest = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrEmpty(webSdkAbs)) {
                if (Directory.Exists(webSdkAbs)) {
                    plan.Entries.Add(new Entry {
                        SourceAbs = webSdkAbs, DestRel = StagedRoot + "/web",
                        Reason = "pmc.js SDK (served at /pmc/)"
                    });
                    takenDest[StagedRoot + "/web"] = webSdkAbs;
                } else {
                    plan.Warnings.Add(webSdkAbs + " is missing; /pmc/pmc.js will 404 in the build");
                }
            }

            if (candidates == null) return plan;
            var streamingAbs = Path.GetFullPath(Path.Combine(projectRoot, "Assets", "StreamingAssets"));
            foreach (var cand in candidates) {
                var rel = NormalizeRel(cand.Rel);
                var reason = string.IsNullOrEmpty(cand.Reason) ? "ControllerDir " + cand.Rel : cand.Reason;
                if (rel == "") continue;                          // "" disables file serving
                if (rel == "Assets") {
                    plan.Warnings.Add(reason + " points at Assets/ itself; refusing to stage the whole project");
                    continue;
                }
                if (!rel.StartsWith("Assets/", StringComparison.Ordinal)) {
                    plan.Warnings.Add(reason + " (" + cand.Rel + ") is not under Assets/ — it won't exist in a " +
                        "player build; move it under Assets/ or use a path valid on the target machine");
                    continue;
                }
                var abs = Path.GetFullPath(Path.Combine(projectRoot, rel));
                if (abs.StartsWith(streamingAbs + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    continue;                                     // already inside StreamingAssets — ships as-is
                if (!Directory.Exists(abs)) {
                    plan.Warnings.Add(reason + " (" + rel + ") does not exist — skipped");
                    continue;
                }
                var dest = StagedRoot + "/" + Path.GetFileName(rel);
                if (takenDest.TryGetValue(dest, out var prev)) {
                    if (!PathEquals(prev, abs))
                        plan.Warnings.Add(reason + " (" + rel + ") stages to " + dest + ", already taken by " +
                            prev + "; rename one of them");
                    continue;
                }
                takenDest[dest] = abs;
                plan.Entries.Add(new Entry { SourceAbs = abs, DestRel = dest, Reason = reason });
            }
            return plan;
        }

        /// <summary>
        /// Scan C# sources under <paramref name="dirAbs"/> for string literals passed to
        /// <c>ServeDirectory("…", "Assets/…")</c> or assigned to <c>ControllerDir</c> —
        /// covers hosts built in code, which scene scanning can't see. Port of the
        /// .gd literal scan in the Godot export plugin.
        /// </summary>
        public static void ScanSourceLiterals(string dirAbs, List<SourceDir> outDirs) {
            if (!Directory.Exists(dirAbs)) return;
            foreach (var file in EnumerateSourceFiles(dirAbs)) {
                string text;
                try { text = File.ReadAllText(file); } catch (IOException) { continue; }
                var name = Path.GetFileName(file);
                foreach (Match m in ServeDirectoryRe.Matches(text))
                    outDirs.Add(new SourceDir { Rel = m.Groups[1].Value, Reason = "ServeDirectory() literal in " + name });
                foreach (Match m in ControllerDirRe.Matches(text))
                    outDirs.Add(new SourceDir { Rel = m.Groups[1].Value, Reason = "ControllerDir literal in " + name });
            }
        }

        /// <summary>Copy every entry into Assets/StreamingAssets, recording what was
        /// created/overwritten into <paramref name="manifest"/>. Returns files copied.</summary>
        public static int Stage(Plan plan, string projectRoot, string backupDirAbs, Manifest manifest) {
            var seenDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int copied = 0;
            foreach (var e in plan.Entries) {
                var destAbs = Path.Combine(projectRoot, "Assets", "StreamingAssets",
                    e.DestRel.Replace('/', Path.DirectorySeparatorChar));
                copied += CopyTree(e.SourceAbs, destAbs, projectRoot, backupDirAbs, manifest, seenDirs);
                if (copied >= MaxFiles) break;
            }
            return copied;
        }

        /// <summary>Serialize the manifest so cleanup still works after a domain reload.</summary>
        public static void SaveManifest(Manifest mf, string path) {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            using (var w = new StreamWriter(path)) {
                foreach (var kv in mf.Overwritten) w.WriteLine("O\t" + kv.Key + "\t" + kv.Value);
                foreach (var f in mf.CreatedFiles) w.WriteLine("F\t" + f);
                foreach (var d in mf.CreatedDirs) w.WriteLine("D\t" + d);
            }
        }

        /// <summary>
        /// Undo <see cref="Stage"/>: restore overwritten files, delete created files
        /// (and their .meta companions), then remove created dirs deepest-first.
        /// Best-effort: missing paths are skipped, the manifest is always removed.
        /// </summary>
        public static void Clean(string manifestPath, string projectRoot) {
            var mf = new Manifest();
            try {
                foreach (var line in File.ReadAllLines(manifestPath)) {
                    var parts = line.Split('\t');
                    if (parts.Length < 2) continue;
                    if (parts[0] == "O" && parts.Length >= 3) mf.Overwritten.Add(new KeyValuePair<string, string>(parts[1], parts[2]));
                    else if (parts[0] == "F") mf.CreatedFiles.Add(parts[1]);
                    else if (parts[0] == "D") mf.CreatedDirs.Add(parts[1]);
                }
            } catch (IOException) { return; }

            foreach (var kv in mf.Overwritten) {
                var dest = Path.Combine(projectRoot, kv.Key.Replace('/', Path.DirectorySeparatorChar));
                try {
                    if (File.Exists(kv.Value)) File.Copy(kv.Value, dest, true);
                } catch (Exception) { /* keep staged copy rather than lose the original */ }
            }
            foreach (var f in mf.CreatedFiles) {
                var abs = Path.Combine(projectRoot, f.Replace('/', Path.DirectorySeparatorChar));
                TryDeleteFile(abs);
                TryDeleteFile(abs + ".meta");
            }
            var dirs = new List<string>(mf.CreatedDirs);
            dirs.Sort((a, b) => b.Length - a.Length); // deepest first
            foreach (var d in dirs) {
                var abs = Path.Combine(projectRoot, d.Replace('/', Path.DirectorySeparatorChar));
                try { if (Directory.Exists(abs)) Directory.Delete(abs, true); } catch (Exception) { }
                TryDeleteFile(abs + ".meta");
            }
            TryDeleteFile(manifestPath);
            var backupDir = mf.Overwritten.Count > 0 ? Path.GetDirectoryName(mf.Overwritten[0].Value) : null;
            if (!string.IsNullOrEmpty(backupDir)) {
                try { if (Directory.Exists(backupDir)) Directory.Delete(backupDir, true); } catch (Exception) { }
            }
        }

        static int CopyTree(string srcAbs, string dstAbs, string projectRoot, string backupDirAbs,
                Manifest mf, HashSet<string> seenDirs) {
            int copied = 0;
            var stack = new Stack<string>();
            stack.Push(srcAbs);
            while (stack.Count > 0 && copied < MaxFiles) {
                var dir = stack.Pop();
                string rel = dir.Length == srcAbs.Length ? "" :
                    dir.Substring(srcAbs.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string[] files, subs;
                try {
                    files = Directory.GetFiles(dir);
                    subs = Directory.GetDirectories(dir);
                } catch (Exception) { continue; }
                foreach (var f in files) {
                    var name = Path.GetFileName(f);
                    if (name.StartsWith(".") || name.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                        continue;                                        // import metadata is not servable content
                    var dstFile = string.IsNullOrEmpty(rel) ? Path.Combine(dstAbs, name)
                        : Path.Combine(dstAbs, rel, name);
                    EnsureDir(Path.GetDirectoryName(dstFile), projectRoot, mf, seenDirs);
                    CopyOne(f, dstFile, projectRoot, backupDirAbs, mf);
                    if (++copied >= MaxFiles) break;
                }
                foreach (var s in subs)
                    if (!Path.GetFileName(s).StartsWith(".")) stack.Push(s);
            }
            return copied;
        }

        static void CopyOne(string src, string dst, string projectRoot, string backupDirAbs, Manifest mf) {
            var rel = RelToProject(dst, projectRoot);
            if (File.Exists(dst)) {
                Directory.CreateDirectory(backupDirAbs);
                var backup = Path.Combine(backupDirAbs, mf.Overwritten.Count.ToString("D4"));
                File.Copy(dst, backup, true);
                mf.Overwritten.Add(new KeyValuePair<string, string>(rel, backup));
            } else {
                mf.CreatedFiles.Add(rel);
            }
            File.Copy(src, dst, true);
        }

        static void EnsureDir(string abs, string projectRoot, Manifest mf, HashSet<string> seenDirs) {
            if (string.IsNullOrEmpty(abs) || Directory.Exists(abs) || !seenDirs.Add(abs)) return;
            EnsureDir(Path.GetDirectoryName(abs), projectRoot, mf, seenDirs);
            if (!Directory.Exists(abs)) {
                Directory.CreateDirectory(abs);
                mf.CreatedDirs.Add(RelToProject(abs, projectRoot));
            }
        }

        static IEnumerable<string> EnumerateSourceFiles(string dirAbs) {
            var stack = new Stack<string>();
            stack.Push(dirAbs);
            while (stack.Count > 0) {
                var dir = stack.Pop();
                string[] files, subs;
                try {
                    files = Directory.GetFiles(dir, "*.cs");
                    subs = Directory.GetDirectories(dir);
                } catch (Exception) { continue; }
                foreach (var f in files) yield return f;
                foreach (var s in subs)
                    if (!Path.GetFileName(s).StartsWith(".")) stack.Push(s);
            }
        }

        static string NormalizeRel(string p) {
            if (string.IsNullOrEmpty(p)) return "";
            return p.Trim().Replace('\\', '/').TrimEnd('/');
        }

        static string RelToProject(string abs, string projectRoot) {
            var rel = Path.GetFullPath(abs);
            var root = Path.GetFullPath(projectRoot);
            if (rel.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                rel = rel.Substring(root.Length + 1);
            return rel.Replace(Path.DirectorySeparatorChar, '/');
        }

        static bool PathEquals(string a, string b) {
            return string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
        }

        static void TryDeleteFile(string path) {
            try { if (File.Exists(path)) File.Delete(path); } catch (Exception) { }
        }
    }
}
