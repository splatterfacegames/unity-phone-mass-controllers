using System;
using System.Collections.Generic;
using System.Text;

namespace Splatter.Pmc {
    /// <summary>
    /// HTTP/1.x request-head parsing helpers. Internal. Pure functions, no I/O.
    /// Port of http_parser.gd.
    /// </summary>
    internal static class PmcHttpParser {
        private const string TCHAR = "!#$%&'*+-.^_`|~0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";

        // Strict UTF-8: throws on invalid sequences (Godot's get_string_from_utf8 + round-trip check).
        internal static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
        // Byte→char identity for header blocks (HTTP/1.x historically ISO-8859-1).
        internal static readonly Encoding Latin1 = Encoding.Latin1;

        /// <summary>
        /// Finds the end of the header block in <paramref name="buf"/>, starting at <paramref name="from"/>
        /// (scanning at most <paramref name="limit"/> bytes). Returns [head_end, body_start],
        /// or [-1, -1] if it isn't complete yet. Accepts CRLF CRLF, and bare LF LF for lenient clients.
        /// </summary>
        internal static int FindHeadEnd(byte[] buf, int from, int limit, out int bodyStart) {
            int end = Math.Min(buf.Length, from + limit);
            int i = IndexOf(buf, (byte)'\n', from);
            while (i >= 0 && i < end) {
                // i points at a LF. Check for LF LF or CRLF CRLF ending here.
                if (i + 1 < buf.Length && buf[i + 1] == (byte)'\n') {
                    bodyStart = i + 2;
                    return i;
                }
                if (i + 2 < buf.Length && buf[i + 1] == (byte)'\r' && buf[i + 2] == (byte)'\n') {
                    bodyStart = i + 3;
                    return i;
                }
                i = IndexOf(buf, (byte)'\n', i + 1);
            }
            bodyStart = -1;
            return -1;
        }

        private static int IndexOf(byte[] buf, byte b, int from) {
            for (int i = Math.Max(0, from); i < buf.Length; i++) {
                if (buf[i] == b) return i;
            }
            return -1;
        }

        /// <summary>
        /// Parses the request line plus headers (the text before the blank line).
        /// Returns a result with <see cref="PmcHttpParseResult.Ok"/> true and Request set,
        /// or Ok false with Status/Reason.
        /// </summary>
        internal static PmcHttpParseResult ParseHead(string head) {
            var lines = head.Split('\n');
            for (int i = 0; i < lines.Length; i++) {
                if (lines[i].EndsWith("\r", StringComparison.Ordinal)) {
                    lines[i] = lines[i].Substring(0, lines[i].Length - 1);
                }
            }
            // RFC 9112 2.2: ignore at least one empty line before the request line.
            int first = 0;
            while (first < lines.Length && lines[first] == "") first++;
            if (first >= lines.Length) {
                return PmcHttpParseResult.Err(400, "empty request");
            }
            var parts = lines[first].Split(' ');
            if (parts.Length != 3) {
                return PmcHttpParseResult.Err(400, "malformed request line");
            }
            var req = new PmcHttpRequest();
            req.Method = parts[0];
            req.RawTarget = parts[1];
            req.Version = parts[2];
            if (req.Method == "" || !IsToken(req.Method)) {
                return PmcHttpParseResult.Err(400, "bad method");
            }
            if (req.Version != "HTTP/1.1" && req.Version != "HTTP/1.0") {
                if (req.Version.StartsWith("HTTP/", StringComparison.Ordinal)) {
                    return PmcHttpParseResult.Err(505, "unsupported HTTP version");
                }
                return PmcHttpParseResult.Err(400, "bad version");
            }
            if (req.RawTarget.Length > 8192) {
                return PmcHttpParseResult.Err(414, "URI too long");
            }

            string target = req.RawTarget;
            if (target.StartsWith("http://", StringComparison.Ordinal) || target.StartsWith("https://", StringComparison.Ordinal)) {
                int after = target.IndexOf('/', target.IndexOf("//", StringComparison.Ordinal) + 2);
                target = after < 0 ? "/" : target.Substring(after);
            }
            if (!target.StartsWith("/", StringComparison.Ordinal)) {
                return PmcHttpParseResult.Err(400, "bad request target");
            }
            foreach (char c in target) {
                if (c <= 32 || c >= 127) {
                    return PmcHttpParseResult.Err(400, "bad character in target");
                }
            }
            int hash = target.IndexOf('#');
            if (hash >= 0) {
                target = target.Substring(0, hash);
            }
            int q = target.IndexOf('?');
            req.RawPath = q < 0 ? target : target.Substring(0, q);
            req.QueryString = q < 0 ? "" : target.Substring(q + 1);
            string decoded = PercentDecode(req.RawPath, false);
            if (decoded == null) {
                return PmcHttpParseResult.Err(400, "bad percent-encoding");
            }
            req.Path = decoded;
            req.Query = ParseQuery(req.QueryString);

            for (int i = first + 1; i < lines.Length; i++) {
                string line = lines[i];
                if (line == "") continue;
                if (line[0] == ' ' || line[0] == '\t') {
                    return PmcHttpParseResult.Err(400, "obsolete header folding");
                }
                int colon = line.IndexOf(':');
                if (colon <= 0) {
                    return PmcHttpParseResult.Err(400, "malformed header");
                }
                string name = line.Substring(0, colon);
                if (!IsToken(name)) {
                    return PmcHttpParseResult.Err(400, "bad header name");
                }
                string value = line.Substring(colon + 1).Trim();
                string key = name.ToLowerInvariant();
                if (req.Headers.ContainsKey(key)) {
                    if (key == "content-length" || key == "host") {
                        if (req.Headers[key] != value) {
                            return PmcHttpParseResult.Err(400, "conflicting " + key);
                        }
                        continue;
                    }
                    req.Headers[key] = req.Headers[key] + ", " + value;
                } else {
                    req.Headers[key] = value;
                }
            }
            if (req.Version == "HTTP/1.1" && !req.Headers.ContainsKey("host")) {
                return PmcHttpParseResult.Err(400, "missing Host header");
            }
            return PmcHttpParseResult.Ok(req);
        }

        /// <summary>
        /// Percent-decodes <paramref name="s"/> as UTF-8. Returns null for malformed escapes, invalid
        /// UTF-8, or (in paths) encoded NUL. When <paramref name="plusIsSpace"/> is set, '+' becomes a
        /// space (query strings).
        /// </summary>
        internal static string PercentDecode(string s, bool plusIsSpace) {
            if (s.IndexOf('%') < 0 && (!plusIsSpace || s.IndexOf('+') < 0)) {
                return s;
            }
            byte[] src = StrictUtf8.GetBytes(s);
            var outp = new byte[src.Length];
            int outLen = 0;
            int i = 0;
            int n = src.Length;
            while (i < n) {
                byte c = src[i];
                if (c == 37) { // %
                    if (i + 2 >= n) return null;
                    int hi = Hex(src[i + 1]);
                    int lo = Hex(src[i + 2]);
                    if (hi < 0 || lo < 0) return null;
                    int v = hi * 16 + lo;
                    if (v == 0) return null;
                    outp[outLen++] = (byte)v;
                    i += 3;
                } else if (c == 43 && plusIsSpace) {
                    outp[outLen++] = 32;
                    i += 1;
                } else {
                    outp[outLen++] = c;
                    i += 1;
                }
            }
            try {
                return StrictUtf8.GetString(outp, 0, outLen);
            } catch (DecoderFallbackException) {
                return null;
            }
        }

        /// <summary>Parses <c>a=1&amp;b=x%20y</c> into a dictionary. Malformed pairs are skipped;
        /// a repeated key keeps the last value.</summary>
        internal static Dictionary<string, string> ParseQuery(string qs) {
            var d = new Dictionary<string, string>();
            if (qs == "") return d;
            foreach (string pair in qs.Split('&')) {
                if (pair == "") continue;
                int eq = pair.IndexOf('=');
                string k = PercentDecode(eq < 0 ? pair : pair.Substring(0, eq), true);
                string v = eq < 0 ? "" : PercentDecode(pair.Substring(eq + 1), true);
                if (k == null || v == null) continue;
                d[k] = v;
            }
            return d;
        }

        private static int Hex(int c) {
            if (c >= 48 && c <= 57) return c - 48;
            if (c >= 97 && c <= 102) return c - 87;
            if (c >= 65 && c <= 70) return c - 55;
            return -1;
        }

        private static bool IsToken(string s) {
            foreach (char c in s) {
                if (TCHAR.IndexOf(c) < 0) return false;
            }
            return true;
        }
    }

    /// <summary>Result of <see cref="PmcHttpParser.ParseHead"/>.</summary>
    internal sealed class PmcHttpParseResult {
        internal bool IsOk;
        internal PmcHttpRequest Request;
        internal int Status;
        internal string Reason = "";

        internal static PmcHttpParseResult Ok(PmcHttpRequest req) {
            return new PmcHttpParseResult { IsOk = true, Request = req };
        }

        internal static PmcHttpParseResult Err(int status, string reason) {
            return new PmcHttpParseResult { IsOk = false, Status = status, Reason = reason };
        }
    }
}
