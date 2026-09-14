using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Splatter.Pmc {
    /// <summary>
    /// An HTTP response returned from a <see cref="PmcHostCore.AddRoute"/> handler, or built by the host.
    /// Port of http_response.gd.
    ///
    /// Use the static constructors (<see cref="Text"/>, <see cref="Json"/>, <see cref="Bytes"/>,
    /// <see cref="File"/>, <see cref="Error"/>, <see cref="Redirect"/>) and chain
    /// <see cref="SetHeader"/>. File responses are streamed in chunks, so a large file doesn't block
    /// the frame.
    /// </summary>
    public sealed class PmcHttpResponse {
        /// <summary>Status code.</summary>
        public int Status = 200;
        /// <summary>Response headers (name to value). Content-Length and Connection are set by the host.</summary>
        public Dictionary<string, string> Headers = new Dictionary<string, string>();
        /// <summary>In-memory body. Ignored when <see cref="FilePath"/> is set.</summary>
        public byte[] Body = new byte[0];
        /// <summary>When non-empty, the body is streamed from this file (an absolute path).</summary>
        public string FilePath = "";
        /// <summary>Byte offset into <see cref="FilePath"/> to start streaming from.</summary>
        public long FileOffset;
        /// <summary>Bytes to stream from <see cref="FilePath"/>. -1 means to the end of the file.</summary>
        public long FileLength = -1;

        /// <summary>Sets a header and returns <c>this</c> for chaining.</summary>
        public PmcHttpResponse SetHeader(string name, string value) {
            string found = null;
            foreach (var k in Headers.Keys) {
                if (string.Equals(k, name, StringComparison.OrdinalIgnoreCase)) found = k;
            }
            if (found != null) Headers.Remove(found);
            Headers[name] = value;
            return this;
        }

        /// <summary>Returns a header value (case-insensitive), or "".</summary>
        public string GetHeader(string name) {
            foreach (var kv in Headers) {
                if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)) return kv.Value;
            }
            return "";
        }

        /// <summary>A UTF-8 text response.</summary>
        public static PmcHttpResponse Text(int status, string content, string contentType = "text/plain; charset=utf-8") {
            var r = new PmcHttpResponse();
            r.Status = status;
            r.Body = Encoding.UTF8.GetBytes(content);
            r.Headers["Content-Type"] = contentType;
            return r;
        }

        /// <summary>A UTF-8 text response (status-first argument order also supported).</summary>
        public static PmcHttpResponse Text(string content, int code = 200, string contentType = "text/plain; charset=utf-8") {
            return Text(code, content, contentType);
        }

        /// <summary>An HTML response with Cache-Control: no-cache.</summary>
        public static PmcHttpResponse Html(string content, int code = 200) {
            return Text(code, content, "text/html; charset=utf-8").SetHeader("Cache-Control", "no-cache");
        }

        /// <summary>A JSON response (<see cref="JsonConvert.SerializeObject(object)"/>) with
        /// Cache-Control: no-cache.</summary>
        public static PmcHttpResponse Json(JToken data, int code = 200) {
            return Text(code, data == null ? "null" : data.ToString(Formatting.None),
                "application/json; charset=utf-8").SetHeader("Cache-Control", "no-cache");
        }

        /// <summary>A JSON response for any serializable object.</summary>
        public static PmcHttpResponse JsonObject(object data, int code = 200) {
            return Text(code, JsonConvert.SerializeObject(data, Formatting.None),
                "application/json; charset=utf-8").SetHeader("Cache-Control", "no-cache");
        }

        /// <summary>A binary response.</summary>
        public static PmcHttpResponse Bytes(byte[] data, string contentType = "application/octet-stream", int code = 200) {
            var r = new PmcHttpResponse();
            r.Status = code;
            r.Body = data ?? new byte[0];
            r.Headers["Content-Type"] = contentType;
            return r;
        }

        /// <summary>A streamed file response. Returns null if the file can't be opened. The MIME type
        /// comes from the extension. Use <see cref="PmcStaticFiles.FileResponse"/> if you also want
        /// Range support.</summary>
        public static PmcHttpResponse File(string path, string contentType = "") {
            if (!System.IO.File.Exists(path)) return null;
            var r = new PmcHttpResponse();
            r.FilePath = path;
            r.Headers["Content-Type"] = contentType != "" ? contentType : PmcStaticFiles.MimeFor(path);
            if (PmcStaticFiles.IsNoCache(path)) {
                r.Headers["Cache-Control"] = "no-cache";
            }
            return r;
        }

        /// <summary>A plain-text error page for <paramref name="code"/>.</summary>
        public static PmcHttpResponse Error(int code, string message = "") {
            string msg = message != "" ? message : StatusText(code);
            return Text(code, code + " " + msg + "\n");
        }

        /// <summary>404 Not Found.</summary>
        public static PmcHttpResponse NotFound(string message = "") {
            return Error(404, message);
        }

        /// <summary>A redirect to <paramref name="location"/>.</summary>
        public static PmcHttpResponse Redirect(string location, int code = 302) {
            return Text(code, "").SetHeader("Location", location);
        }

        /// <summary>Reason phrase for a status code.</summary>
        public static string StatusText(int code) {
            switch (code) {
                case 101: return "Switching Protocols";
                case 200: return "OK";
                case 204: return "No Content";
                case 206: return "Partial Content";
                case 301: return "Moved Permanently";
                case 302: return "Found";
                case 304: return "Not Modified";
                case 400: return "Bad Request";
                case 403: return "Forbidden";
                case 404: return "Not Found";
                case 405: return "Method Not Allowed";
                case 408: return "Request Timeout";
                case 411: return "Length Required";
                case 413: return "Content Too Large";
                case 414: return "URI Too Long";
                case 416: return "Range Not Satisfiable";
                case 426: return "Upgrade Required";
                case 429: return "Too Many Requests";
                case 431: return "Request Header Fields Too Large";
                case 500: return "Internal Server Error";
                case 501: return "Not Implemented";
                case 503: return "Service Unavailable";
                case 505: return "HTTP Version Not Supported";
            }
            return "Status";
        }

        /// <summary>Serializes the status line and headers. Internal: used by the host.</summary>
        internal byte[] BuildHead(long contentLength, bool keepAlive) {
            var sb = new StringBuilder();
            sb.Append("HTTP/1.1 ").Append(Status).Append(' ').Append(StatusText(Status)).Append("\r\n");
            foreach (var kv in Headers) {
                string lk = kv.Key.ToLowerInvariant();
                if (lk == "content-length" || (lk == "connection" && Status != 101)) continue;
                sb.Append(kv.Key).Append(": ").Append(Clean(kv.Value)).Append("\r\n");
            }
            if (Status != 101 && Status != 204 && Status != 304) {
                sb.Append("Content-Length: ").Append(contentLength).Append("\r\n");
            }
            if (Status != 101) {
                sb.Append("Connection: ").Append(keepAlive ? "keep-alive" : "close").Append("\r\n");
            }
            sb.Append("\r\n");
            return Encoding.UTF8.GetBytes(sb.ToString());
        }

        private static string Clean(string v) {
            return v.Replace("\r", "").Replace("\n", "");
        }
    }
}
