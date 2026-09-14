using System;
using System.Security.Cryptography;
using System.Text;

namespace Splatter.Pmc {
    /// <summary>
    /// RFC 6455 frame encoding helpers. Internal. Server frames are unmasked.
    /// Masked encoding exists for test clients. Port of ws_frame.gd.
    /// </summary>
    internal static class PmcWsFrame {
        internal const int OpContinuation = 0x0;
        internal const int OpText = 0x1;
        internal const int OpBinary = 0x2;
        internal const int OpClose = 0x8;
        internal const int OpPing = 0x9;
        internal const int OpPong = 0xA;

        private const string Guid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

        /// <summary>Encodes one frame. When <paramref name="maskKey"/> has 4 bytes, the payload is
        /// masked (client-style).</summary>
        internal static byte[] Encode(int opcode, byte[] payload, bool fin = true, byte[] maskKey = null) {
            int n = payload.Length;
            bool masked = maskKey != null && maskKey.Length == 4;
            int headLen = 2 + (n < 126 ? 0 : n < 65536 ? 2 : 8) + (masked ? 4 : 0);
            var outp = new byte[headLen + n];
            int i = 0;
            outp[i++] = (byte)((fin ? 0x80 : 0) | (opcode & 0x0F));
            int mbit = masked ? 0x80 : 0;
            if (n < 126) {
                outp[i++] = (byte)(mbit | n);
            } else if (n < 65536) {
                outp[i++] = (byte)(mbit | 126);
                outp[i++] = (byte)((n >> 8) & 0xFF);
                outp[i++] = (byte)(n & 0xFF);
            } else {
                outp[i++] = (byte)(mbit | 127);
                for (int s = 7; s >= 0; s--) {
                    outp[i++] = (byte)((n >> (s * 8)) & 0xFF);
                }
            }
            if (masked) {
                Buffer.BlockCopy(maskKey, 0, outp, i, 4);
                i += 4;
                XorMaskInto(payload, 0, n, maskKey, 0, outp, i);
            } else {
                Buffer.BlockCopy(payload, 0, outp, i, n);
            }
            return outp;
        }

        /// <summary>A text frame.</summary>
        internal static byte[] Text(string s) {
            return Encode(OpText, PmcHttpParser.StrictUtf8.GetBytes(s));
        }

        /// <summary>A binary frame.</summary>
        internal static byte[] Binary(byte[] data) {
            return Encode(OpBinary, data);
        }

        /// <summary>A close frame with <paramref name="code"/> and a UTF-8 <paramref name="reason"/>
        /// (truncated so the payload fits 125 bytes).</summary>
        internal static byte[] Close(int code, string reason = "") {
            byte[] p;
            if (code > 0) {
                var r = PmcHttpParser.StrictUtf8.GetBytes(reason ?? "");
                if (r.Length > 123) {
                    Array.Resize(ref r, 123);
                    // Don't cut through a multi-byte sequence.
                    while (r.Length > 0 && (r[r.Length - 1] & 0xC0) == 0x80) {
                        Array.Resize(ref r, r.Length - 1);
                    }
                    if (r.Length > 0 && (r[r.Length - 1] & 0xC0) == 0xC0) {
                        Array.Resize(ref r, r.Length - 1);
                    }
                }
                p = new byte[2 + r.Length];
                p[0] = (byte)((code >> 8) & 0xFF);
                p[1] = (byte)(code & 0xFF);
                Buffer.BlockCopy(r, 0, p, 2, r.Length);
            } else {
                p = new byte[0];
            }
            return Encode(OpClose, p);
        }

        /// <summary>A ping frame.</summary>
        internal static byte[] Ping(byte[] data = null) {
            return Encode(OpPing, data ?? new byte[0]);
        }

        /// <summary>A pong frame.</summary>
        internal static byte[] Pong(byte[] data = null) {
            return Encode(OpPong, data ?? new byte[0]);
        }

        /// <summary>Sec-WebSocket-Accept value for a client key.</summary>
        internal static string AcceptKey(string key) {
            using (var sha1 = SHA1.Create()) {
                return Convert.ToBase64String(sha1.ComputeHash(PmcHttpParser.StrictUtf8.GetBytes(key + Guid)));
            }
        }

        /// <summary>Whether <paramref name="code"/> may be sent in a close frame (RFC 6455 7.4).</summary>
        internal static bool IsValidCloseCode(int code) {
            if (code >= 3000 && code <= 4999) return true;
            switch (code) {
                case 1000: case 1001: case 1002: case 1003: case 1007: case 1008: case 1009:
                case 1010: case 1011: case 1012: case 1013: case 1014:
                    return true;
            }
            return false;
        }

        /// <summary>
        /// XORs <paramref name="data"/> with the 4-byte <paramref name="key"/> (masking is symmetric).
        /// <paramref name="offset"/> is the position of <c>data[0]</c> within the whole payload
        /// (for chunked unmasking).
        /// </summary>
        internal static byte[] XorMask(byte[] data, byte[] key, int offset = 0) {
            int n = data.Length;
            if (n == 0) return data;
            var outp = new byte[n];
            XorMaskInto(data, 0, n, key, offset, outp, 0);
            return outp;
        }

        internal static void XorMaskInto(byte[] src, int srcOff, int n, byte[] key, int payloadOffset,
                byte[] dst, int dstOff) {
            int k = payloadOffset & 3;
            if (k != 0) {
                // Rotate the effective key so dst[0] uses key[(payloadOffset) & 3].
                int i = srcOff;
                int o = dstOff;
                int p = payloadOffset;
                for (int end = srcOff + n; i < end; i++, o++, p++) {
                    dst[o] = (byte)(src[i] ^ key[p & 3]);
                }
                return;
            }
            int word = key[0] | (key[1] << 8) | (key[2] << 16) | (key[3] << 24);
            int j = srcOff;
            int d = dstOff;
            int limit = srcOff + (n & ~3);
            while (j < limit) {
                int v = src[j] | (src[j + 1] << 8) | (src[j + 2] << 16) | (src[j + 3] << 24);
                v ^= word;
                dst[d] = (byte)v;
                dst[d + 1] = (byte)(v >> 8);
                dst[d + 2] = (byte)(v >> 16);
                dst[d + 3] = (byte)(v >> 24);
                j += 4;
                d += 4;
            }
            for (; j < srcOff + n; j++, d++) {
                dst[d] = (byte)(src[j] ^ key[(j - srcOff) & 3]);
            }
        }
    }
}
