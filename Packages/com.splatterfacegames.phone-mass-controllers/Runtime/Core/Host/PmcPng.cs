using System;
using System.IO;

namespace Splatter.Pmc {
    /// <summary>
    /// Minimal PNG encoder with no engine dependencies. Used by the /pmc/qr.png route and by
    /// <c>PmcQr.EncodePng</c>. Output is a valid but uncompressed image (zlib "stored" deflate
    /// blocks) — QR codes are tiny, so simplicity beats compression here.
    /// </summary>
    public static class PmcPng {
        /// <summary>Encodes an 8-bit grayscale image (<paramref name="gray"/> is
        /// <c>width*height</c> bytes, row-major, 0=black).</summary>
        public static byte[] EncodeGray8(int width, int height, byte[] gray) {
            if (gray == null || gray.Length < width * height) {
                throw new ArgumentException("gray must hold width*height bytes", nameof(gray));
            }
            // Scanlines: filter byte 0 per row.
            var raw = new byte[(width + 1) * height];
            for (int y = 0; y < height; y++) {
                Buffer.BlockCopy(gray, y * width, raw, y * (width + 1) + 1, width);
            }
            return Wrap(width, height, 0, 8, raw); // colour type 0 (grayscale), bit depth 8
        }

        /// <summary>Encodes an RGBA image (<paramref name="rgba"/> is <c>width*height*4</c> bytes,
        /// row-major).</summary>
        public static byte[] EncodeRgba(int width, int height, byte[] rgba) {
            if (rgba == null || rgba.Length < width * height * 4) {
                throw new ArgumentException("rgba must hold width*height*4 bytes", nameof(rgba));
            }
            var raw = new byte[(width * 4 + 1) * height];
            for (int y = 0; y < height; y++) {
                Buffer.BlockCopy(rgba, y * width * 4, raw, y * (width * 4 + 1) + 1, width * 4);
            }
            return Wrap(width, height, 6, 8, raw); // colour type 6 (RGBA), bit depth 8
        }

        /// <summary>Renders a module matrix (true = dark) to a PNG: <paramref name="modulePx"/> pixels
        /// per module plus a <paramref name="quiet"/>-module white border. This is the exact image the
        /// /pmc/qr.png route serves.</summary>
        public static byte[] EncodeBitmap(bool[,] dark, int modulePx, int quiet) {
            int mw = dark.GetLength(0);
            int mh = dark.GetLength(1);
            if (modulePx < 1) modulePx = 1;
            if (quiet < 0) quiet = 0;
            int w = (mw + quiet * 2) * modulePx;
            int h = (mh + quiet * 2) * modulePx;
            var gray = new byte[w * h];
            for (int i = 0; i < gray.Length; i++) gray[i] = 255;
            for (int my = 0; my < mh; my++) {
                for (int mx = 0; mx < mw; mx++) {
                    if (!dark[mx, my]) continue;
                    int x0 = (mx + quiet) * modulePx;
                    int y0 = (my + quiet) * modulePx;
                    for (int yy = 0; yy < modulePx; yy++) {
                        int row = (y0 + yy) * w;
                        for (int xx = 0; xx < modulePx; xx++) {
                            gray[row + x0 + xx] = 0;
                        }
                    }
                }
            }
            return EncodeGray8(w, h, gray);
        }

        // ---- PNG container ----

        private static byte[] Wrap(int width, int height, int colorType, int bitDepth, byte[] scanlines) {
            using (var ms = new MemoryStream()) {
                ms.Write(PngSig, 0, PngSig.Length);
                var ihdr = new byte[13];
                WriteBE32(ihdr, 0, width);
                WriteBE32(ihdr, 4, height);
                ihdr[8] = (byte)bitDepth;
                ihdr[9] = (byte)colorType;
                // 10=deflate, 11=filter none, 12=no interlace
                WriteChunk(ms, "IHDR", ihdr);
                WriteChunk(ms, "IDAT", ZlibStore(scanlines));
                WriteChunk(ms, "IEND", new byte[0]);
                return ms.ToArray();
            }
        }

        private static readonly byte[] PngSig = { 137, 80, 78, 71, 13, 10, 26, 10 };

        private static void WriteChunk(Stream s, string type, byte[] data) {
            var head = new byte[4];
            WriteBE32(head, 0, data.Length);
            s.Write(head, 0, 4);
            var name = new[] { (byte)type[0], (byte)type[1], (byte)type[2], (byte)type[3] };
            s.Write(name, 0, 4);
            s.Write(data, 0, data.Length);
            uint crc = Crc32(name, 0, 4, 0xFFFFFFFF);
            crc = Crc32(data, 0, data.Length, crc);
            WriteBE32(head, 0, (int)(crc ^ 0xFFFFFFFF));
            s.Write(head, 0, 4);
        }

        // zlib stream of uncompressed deflate blocks: 78 01, then per block
        // [BFINAL][LEN lo,hi][NLEN lo,hi][data], then the adler32 of the raw data.
        private static byte[] ZlibStore(byte[] data) {
            using (var ms = new MemoryStream()) {
                ms.WriteByte(0x78);
                ms.WriteByte(0x01);
                int off = 0;
                while (off < data.Length || (data.Length == 0 && off == 0)) {
                    int n = Math.Min(65535, data.Length - off);
                    bool last = off + n >= data.Length;
                    ms.WriteByte((byte)(last ? 1 : 0));
                    ms.WriteByte((byte)(n & 0xFF));
                    ms.WriteByte((byte)((n >> 8) & 0xFF));
                    ms.WriteByte((byte)(~n & 0xFF));
                    ms.WriteByte((byte)((~n >> 8) & 0xFF));
                    ms.Write(data, off, n);
                    off += n;
                    if (data.Length == 0) break;
                }
                uint adler = Adler32(data);
                ms.WriteByte((byte)((adler >> 24) & 0xFF));
                ms.WriteByte((byte)((adler >> 16) & 0xFF));
                ms.WriteByte((byte)((adler >> 8) & 0xFF));
                ms.WriteByte((byte)(adler & 0xFF));
                return ms.ToArray();
            }
        }

        private static void WriteBE32(byte[] b, int off, int v) {
            b[off] = (byte)((v >> 24) & 0xFF);
            b[off + 1] = (byte)((v >> 16) & 0xFF);
            b[off + 2] = (byte)((v >> 8) & 0xFF);
            b[off + 3] = (byte)(v & 0xFF);
        }

        private static uint Adler32(byte[] data) {
            const uint MOD = 65521;
            uint a = 1, b = 0;
            for (int i = 0; i < data.Length; i++) {
                a = (a + data[i]) % MOD;
                b = (b + a) % MOD;
            }
            return (b << 16) | a;
        }

        private static uint[] _crcTable;

        private static uint Crc32(byte[] data, int off, int len, uint crc) {
            if (_crcTable == null) {
                _crcTable = new uint[256];
                for (uint n = 0; n < 256; n++) {
                    uint c = n;
                    for (int k = 0; k < 8; k++) {
                        c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                    }
                    _crcTable[n] = c;
                }
            }
            for (int i = off; i < off + len; i++) {
                crc = _crcTable[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
            }
            return crc;
        }
    }
}
