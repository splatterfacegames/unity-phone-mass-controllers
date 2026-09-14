using System.Collections.Generic;
using System.Text;

namespace Splatter.Pmc {
    /// <summary>
    /// A parsed HTTP/1.x request, as handed to <see cref="PmcHostCore.AddRoute"/> handlers.
    /// Port of http_request.gd.
    /// </summary>
    public sealed class PmcHttpRequest {
        /// <summary>Request method, e.g. "GET".</summary>
        public string Method = "";
        /// <summary>Raw request target as sent (e.g. "/a%20b?x=1").</summary>
        public string RawTarget = "";
        /// <summary>Percent-decoded path without the query (e.g. "/a b"). Always starts with "/".</summary>
        public string Path = "";
        /// <summary>Raw (still percent-encoded) path without the query.</summary>
        public string RawPath = "";
        /// <summary>Raw query string without the leading "?".</summary>
        public string QueryString = "";
        /// <summary>Decoded query parameters. If a key repeats, the last value wins.</summary>
        public Dictionary<string, string> Query = new Dictionary<string, string>();
        /// <summary>Protocol version, "HTTP/1.1" or "HTTP/1.0".</summary>
        public string Version = "HTTP/1.1";
        /// <summary>Headers keyed by lower-case name. Repeated headers are joined with ", ".</summary>
        public Dictionary<string, string> Headers = new Dictionary<string, string>();
        /// <summary>Request body. Only Content-Length bodies are accepted, capped by
        /// <see cref="PmcHostCore.MaxBodyBytes"/>.</summary>
        public byte[] Body = new byte[0];
        /// <summary>Peer IP address (or the trusted CF-Connecting-IP behind a tunnel).</summary>
        public string RemoteAddress = "";
        /// <summary>Peer TCP port.</summary>
        public int RemotePort;
        /// <summary>For routes registered with <see cref="PmcHostCore.AddRoute"/>: the matched prefix.</summary>
        public string RoutePrefix = "";

        /// <summary>Returns the header <paramref name="name"/> (case-insensitive), or
        /// <paramref name="def"/> when it's absent.</summary>
        public string Header(string name, string def = "") {
            string v;
            return Headers.TryGetValue(name.ToLowerInvariant(), out v) ? v : def;
        }

        /// <summary>Whether the header <paramref name="name"/> contains <paramref name="token"/> as a
        /// comma-separated, case-insensitive element.</summary>
        public bool HeaderHasToken(string name, string token) {
            foreach (string part in Header(name).Split(',')) {
                if (string.Equals(part.Trim(), token, System.StringComparison.OrdinalIgnoreCase)) {
                    return true;
                }
            }
            return false;
        }

        /// <summary>The path relative to <see cref="RoutePrefix"/> (for prefix routes).</summary>
        public string SubPath() {
            return Path.StartsWith(RoutePrefix, System.StringComparison.Ordinal)
                ? Path.Substring(RoutePrefix.Length)
                : Path;
        }

        /// <summary>Whether the connection should stay open after the response (HTTP/1.1 default,
        /// or HTTP/1.0 with keep-alive).</summary>
        public bool WantsKeepAlive() {
            if (HeaderHasToken("connection", "close")) return false;
            if (Version == "HTTP/1.0") return HeaderHasToken("connection", "keep-alive");
            return true;
        }

        /// <summary>The body decoded as UTF-8 text.</summary>
        public string BodyText() {
            return Encoding.UTF8.GetString(Body);
        }
    }
}
