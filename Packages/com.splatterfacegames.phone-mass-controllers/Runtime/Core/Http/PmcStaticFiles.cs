using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace Splatter.Pmc {
    /// <summary>
    /// Traversal-safe static file serving helpers (MIME types, path resolution, Range support).
    /// Port of static_files.gd.
    /// </summary>
    internal static class PmcStaticFiles {
        internal static readonly Dictionary<string, string> Mime = new Dictionary<string, string> {
            { "html", "text/html; charset=utf-8" }, { "htm", "text/html; charset=utf-8" },
            { "js", "text/javascript; charset=utf-8" }, { "mjs", "text/javascript; charset=utf-8" },
            { "css", "text/css; charset=utf-8" }, { "json", "application/json; charset=utf-8" },
            { "map", "application/json; charset=utf-8" }, { "webmanifest", "application/manifest+json; charset=utf-8" },
            { "txt", "text/plain; charset=utf-8" }, { "md", "text/markdown; charset=utf-8" },
            { "xml", "application/xml; charset=utf-8" }, { "csv", "text/csv; charset=utf-8" },
            { "ts", "text/plain; charset=utf-8" },
            { "svg", "image/svg+xml" }, { "png", "image/png" }, { "jpg", "image/jpeg" }, { "jpeg", "image/jpeg" },
            { "gif", "image/gif" }, { "webp", "image/webp" }, { "avif", "image/avif" }, { "ico", "image/x-icon" },
            { "bmp", "image/bmp" },
            { "woff", "font/woff" }, { "woff2", "font/woff2" }, { "ttf", "font/ttf" }, { "otf", "font/otf" },
            { "mp3", "audio/mpeg" }, { "ogg", "audio/ogg" }, { "oga", "audio/ogg" }, { "wav", "audio/wav" },
            { "m4a", "audio/mp4" }, { "flac", "audio/flac" }, { "mp4", "video/mp4" }, { "webm", "video/webm" },
            { "ogv", "video/ogg" }, { "wasm", "application/wasm" }, { "pdf", "application/pdf" },
            { "zip", "application/zip" }, { "glb", "model/gltf-binary" }, { "gltf", "model/gltf+json" },
            { "bin", "application/octet-stream" }, { "pck", "application/octet-stream" },
        };

        private static readonly HashSet<string> NoCacheExt = new HashSet<string> {
            "html", "htm", "js", "mjs", "css", "json", "ts", "webmanifest"
        };

        /// <summary>MIME type for a file path (by extension). Defaults to application/octet-stream.</summary>
        internal static string MimeFor(string path) {
            string m;
            return Mime.TryGetValue(ExtensionOf(path), out m) ? m : "application/octet-stream";
        }

        /// <summary>Whether a file of this type gets Cache-Control: no-cache.</summary>
        internal static bool IsNoCache(string path) {
            return NoCacheExt.Contains(ExtensionOf(path));
        }

        private static string ExtensionOf(string path) {
            int slash = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
            int dot = path.LastIndexOf('.');
            if (dot <= slash || dot < 0) return "";
            return path.Substring(dot + 1).ToLowerInvariant();
        }

        /// <summary>
        /// Resolves the decoded URL sub-path <paramref name="rel"/> under <paramref name="root"/>.
        /// Returns "" if it's unsafe: ".." segments, backslashes, colons, NUL or control characters.
        /// Otherwise returns the joined path, with a trailing "/" if <paramref name="rel"/> had one.
        /// </summary>
        internal static string Resolve(string root, string rel) {
            if (root == "") return "";
            var segments = new List<string>();
            foreach (string seg in rel.Split('/')) {
                if (seg == "" || seg == ".") continue;
                if (seg == ".." || seg.IndexOf('\\') >= 0 || seg.IndexOf(':') >= 0) {
                    return "";
                }
                foreach (char c in seg) {
                    if (c < 32 || c == 127) return "";
                }
                // Windows ignores trailing dots/spaces (".. " or "..." can alias the parent), so refuse them.
                if (seg.EndsWith(".", StringComparison.Ordinal) || seg.EndsWith(" ", StringComparison.Ordinal)) {
                    return "";
                }
                segments.Add(seg);
            }
            string b = root;
            if (b.EndsWith("/", StringComparison.Ordinal) && !b.EndsWith("://", StringComparison.Ordinal)) {
                b = b.Substring(0, b.Length - 1);
            }
            string joined = b;
            foreach (string seg in segments) {
                joined = joined + (joined.EndsWith("/", StringComparison.Ordinal) ? "" : "/") + seg;
            }
            if (rel.EndsWith("/", StringComparison.Ordinal) && !joined.EndsWith("/", StringComparison.Ordinal)) {
                joined += "/";
            }
            return joined;
        }

        /// <summary>
        /// Serves <paramref name="rel"/> (the decoded sub-path below the mount prefix) from
        /// <paramref name="root"/> for <paramref name="req"/>. Directories without a trailing slash
        /// redirect (301), and directories serve <paramref name="indexFile"/> ("" = no index).
        /// Returns null when nothing matches, so the caller can fall through to a 404.
        /// </summary>
        internal static PmcHttpResponse Serve(string root, string rel, PmcHttpRequest req, string indexFile = "index.html") {
            string p = Resolve(root, rel);
            if (p == "") {
                if (rel.IndexOf("..", StringComparison.Ordinal) >= 0 || rel.IndexOf('\\') >= 0 || rel.IndexOf(':') >= 0) {
                    return PmcHttpResponse.Error(400, "bad path");
                }
                return null;
            }
            string dirPath = p.EndsWith("/", StringComparison.Ordinal) ? p.Substring(0, p.Length - 1) : p;
            bool isDir = p.EndsWith("/", StringComparison.Ordinal) || rel == "";
            if (!isDir && Directory.Exists(dirPath) && !File.Exists(p)) {
                // Directory without a trailing slash: redirect so relative URLs work.
                if (indexFile != "" && File.Exists(dirPath + "/" + indexFile)) {
                    string loc = req.RawPath + "/";
                    if (req.QueryString != "") loc += "?" + req.QueryString;
                    return PmcHttpResponse.Redirect(loc, 301);
                }
                return null;
            }
            if (isDir) {
                if (indexFile == "") return null;
                p = (p.EndsWith("/", StringComparison.Ordinal) ? p : p + "/") + indexFile;
            }
            if (!File.Exists(p)) return null;
            if (req.Method != "GET" && req.Method != "HEAD") {
                return PmcHttpResponse.Error(405).SetHeader("Allow", "GET, HEAD");
            }
            if (!ConfinedUnder(root, p)) {
                return PmcHttpResponse.Error(403, "path escapes the served directory");
            }
            return FileResponse(p, req);
        }

        /// <summary>
        /// Whether <paramref name="path"/> stays inside <paramref name="root"/> once every symlink
        /// component is resolved (<paramref name="root"/> itself is trusted as the mount point).
        /// </summary>
        internal static bool ConfinedUnder(string root, string path) {
            string rr = CanonicalPath(root);
            string rp = CanonicalPath(path);
            if (rr == "" || rp == "") return false;
            if (!rr.EndsWith("/", StringComparison.Ordinal)) rr += "/";
            bool ignoreCase = IsWindows();
            return string.Equals(rp + "/", rr, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                || rp.StartsWith(rr, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }

        /// <summary>
        /// The absolute path with every symlink/junction component resolved. Components that don't
        /// exist are kept as-is. Returns "" on a link loop or a link that can't be resolved.
        /// </summary>
        internal static string CanonicalPath(string path) {
            string p;
            try {
                p = Path.GetFullPath(path).Replace('\\', '/');
            } catch (Exception) {
                return "";
            }
            for (int i = 0; i < 40; i++) {
                string next = ExpandLinksOnce(p);
                if (next == null) return "";
                if (next == p) return p;
                p = next;
            }
            return "";
        }

        // One resolution pass over each existing component; link targets may themselves contain
        // links, so the caller repeats until the path stops changing.
        private static string ExpandLinksOnce(string path) {
            string normalized = path.Replace('\\', '/');
            var segs = normalized.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            bool unc = normalized.StartsWith("//", StringComparison.Ordinal);
            bool rooted = normalized.StartsWith("/", StringComparison.Ordinal);
            // Rebuild the leading root: "C:", "/", or "//srv/share".
            string cur;
            int startIndex = 0;
            if (unc) {
                if (segs.Length < 2) return normalized;
                cur = "//" + segs[0] + "/" + segs[1];
                startIndex = 2;
            } else if (rooted) {
                cur = "/";
            } else {
                // Drive-relative form: first segment is the "C:" part.
                cur = segs.Length > 0 ? segs[0] : "";
                startIndex = segs.Length > 0 ? 1 : 0;
                if (cur.Length > 0 && !cur.EndsWith(":", StringComparison.Ordinal)) {
                    // No drive or root — shouldn't happen after GetFullPath; treat literally.
                    return normalized;
                }
            }
            bool expanded = false;
            for (int i = startIndex; i < segs.Length; i++) {
                cur = (cur == "" || cur.EndsWith("/", StringComparison.Ordinal)) ? cur + segs[i] : cur + "/" + segs[i];
                string target = ReadLinkTarget(cur);
                if (target == null) continue;           // not a link, or doesn't exist: keep as-is
                if (target == "") return null;           // a link we cannot resolve: fail closed
                target = target.Replace('\\', '/');
                if (target.StartsWith("//?/", StringComparison.Ordinal)) target = target.Substring(4);
                if (target.StartsWith("\\\\?\\", StringComparison.Ordinal)) target = target.Substring(4).Replace('\\', '/');
                bool absolute = target.StartsWith("/", StringComparison.Ordinal)
                    || (target.Length > 2 && target[1] == ':' && target[2] == '/')
                    || target.StartsWith("//", StringComparison.Ordinal);
                string baseDir = cur.Substring(0, Math.Max(0, cur.Length - segs[i].Length - 1));
                string joined = absolute ? target : baseDir + "/" + target;
                try {
                    cur = Path.GetFullPath(joined).Replace('\\', '/');
                } catch (Exception) {
                    return null;
                }
                expanded = true;
            }
            if (!expanded) return normalized;
            return cur == "" ? "/" : cur;
        }

        // Returns null when path is not a link or does not exist; "" when it is a link whose target
        // could not be determined on this runtime; otherwise the link target (possibly relative).
        private static string ReadLinkTarget(string path) {
            FileAttributes attrs;
            try {
                attrs = File.GetAttributes(path);
            } catch (Exception) {
                return null;
            }
            if ((attrs & FileAttributes.ReparsePoint) == 0) return null;
            string target = ResolveViaFramework(path, (attrs & FileAttributes.Directory) != 0);
            if (target != null) return target;
            target = ResolveViaPlatform(path);
            return target ?? "";
        }

        // ---- link target resolution: managed API when present (.NET 6+ / newer Unity BCL), P/Invoke otherwise ----

        private static MethodInfo _resolveLinkTarget;   // FileSystemInfo.ResolveLinkTarget(bool)
        private static bool _resolveProbed;

        private static string ResolveViaFramework(string path, bool isDir) {
            try {
                if (!_resolveProbed) {
                    _resolveProbed = true;
                    _resolveLinkTarget = typeof(FileSystemInfo).GetMethod(
                        "ResolveLinkTarget", new[] { typeof(bool) });
                }
                if (_resolveLinkTarget == null) return null;
                FileSystemInfo fsi = isDir ? (FileSystemInfo)new DirectoryInfo(path) : new FileInfo(path);
                var target = (FileSystemInfo)_resolveLinkTarget.Invoke(fsi, new object[] { false });
                if (target == null) return null;    // reparse point but not a link: keep as-is
                return target.FullName;
            } catch (Exception) {
                return null;
            }
        }

        private static string ResolveViaPlatform(string path) {
            try {
                if (IsWindows()) return ResolveViaWindows(path);
                return ResolveViaReadlink(path);
            } catch (Exception) {
                return null;
            }
        }

        private static bool IsWindows() {
#if NET5_0_OR_GREATER || NETCOREAPP
            return OperatingSystem.IsWindows();
#else
            return Environment.OSVersion.Platform == PlatformID.Win32NT;
#endif
        }

        // -- Windows: open the object (following reparse) and ask for its final path name. --

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandle(IntPtr hFile, StringBuilder lpszFilePath,
            uint cchFilePath, uint dwFlags);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        private const uint FILE_SHARE_ALL = 0x7;
        private const uint OPEN_EXISTING = 3;
        private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
        private const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;

        private static string ResolveViaWindows(string path) {
            IntPtr h = CreateFile(path, 0, FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING,
                FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
            if (h == new IntPtr(-1) || h == IntPtr.Zero) return null;
            try {
                var sb = new StringBuilder(4096);
                uint n = GetFinalPathNameByHandle(h, sb, (uint)sb.Capacity, 0);
                if (n == 0 || n >= (uint)sb.Capacity) return null;
                string s = sb.ToString(0, (int)n);
                if (s.StartsWith("\\\\?\\UNC\\", StringComparison.Ordinal)) s = "//" + s.Substring(8);
                else if (s.StartsWith("\\\\?\\", StringComparison.Ordinal)) s = s.Substring(4);
                return s;
            } finally {
                CloseHandle(h);
            }
        }

        // -- POSIX: readlink(2) gives the immediate link target. --

        [DllImport("libc", SetLastError = true)]
        private static extern IntPtr readlink(string pathname, byte[] buf, IntPtr bufsiz);

        private static string ResolveViaReadlink(string path) {
            var buf = new byte[4096];
            IntPtr n = readlink(path, buf, (IntPtr)buf.Length);
            int len = (int)n;
            if (len <= 0) return null;
            return Encoding.UTF8.GetString(buf, 0, len);
        }

        /// <summary>
        /// A streamed file response with single-range support (Range: bytes=a-b → 206).
        /// Returns null if the file can't be opened.
        /// </summary>
        internal static PmcHttpResponse FileResponse(string path, PmcHttpRequest req = null) {
            long size;
            try {
                size = new FileInfo(path).Length;
            } catch (Exception) {
                return null;
            }
            var r = PmcHttpResponse.File(path);
            if (r == null) return null;
            r.Headers["Accept-Ranges"] = "bytes";
            r.FileLength = size;
            if (req != null && req.Headers.ContainsKey("range")) {
                long[] range = ParseRange(req.Header("range"), size);
                if (range.Length == 0) {
                    return PmcHttpResponse.Error(416).SetHeader("Content-Range", "bytes */" + size);
                }
                if (range[0] >= 0) {
                    r.Status = 206;
                    r.FileOffset = range[0];
                    r.FileLength = range[1] - range[0] + 1;
                    r.Headers["Content-Range"] = "bytes " + range[0] + "-" + range[1] + "/" + size;
                }
            }
            return r;
        }

        /// <summary>
        /// Parses a single "bytes=" range against <paramref name="size"/>. Returns [start, end]
        /// (inclusive), [-1, -1] if the header should be ignored (multi-range or another unit),
        /// or [] if it's unsatisfiable.
        /// </summary>
        internal static long[] ParseRange(string value, long size) {
            long[] ignore = { -1, -1 };
            string v = value.Trim();
            if (!v.StartsWith("bytes=", StringComparison.Ordinal) || v.IndexOf(',') >= 0) return ignore;
            string spec = v.Substring(6).Trim();
            int dash = spec.IndexOf('-');
            if (dash < 0) return ignore;
            string a = spec.Substring(0, dash).Trim();
            string b = spec.Substring(dash + 1).Trim();
            long av = 0, bv = 0;
            if ((a != "" && !long.TryParse(a, out av)) || (b != "" && !long.TryParse(b, out bv))) return ignore;
            long start, end;
            if (a == "") {
                if (b == "") return ignore;
                long suffix = bv;
                if (suffix <= 0 || size == 0) return new long[0];
                start = Math.Max(0, size - suffix);
                end = size - 1;
            } else {
                start = av;
                end = b == "" ? size - 1 : Math.Min(bv, size - 1);
                if (start >= size || end < start) return new long[0];
            }
            return new[] { start, end };
        }
    }
}
