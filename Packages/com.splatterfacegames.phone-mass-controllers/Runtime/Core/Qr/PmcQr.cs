using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Splatter.Pmc
{
    /// <summary>
    /// Pure C# QR Code (Model 2) encoder, ported from PMCQr (qr.gd).
    ///
    /// Supports numeric, alphanumeric and byte (UTF-8) modes, error correction levels
    /// L/M/Q/H, versions 1-40, Reed-Solomon over GF(256), and automatic mask selection
    /// using the standard penalty rules.
    /// <code>
    /// var m = PmcQr.EncodeAdvanced("http://192.168.1.87:8086/?code=ABCD");
    /// byte[] png = PmcQr.EncodePng(m, 8, 4);
    /// </code>
    /// </summary>
    public static class PmcQr
    {
        /// <summary>Error correction level. L ~7%, M ~15%, Q ~25%, H ~30% recovery.</summary>
        public enum Ecc
        {
            /// <summary>~7% recovery.</summary>
            L = 0,
            /// <summary>~15% recovery.</summary>
            M = 1,
            /// <summary>~25% recovery.</summary>
            Q = 2,
            /// <summary>~30% recovery.</summary>
            H = 3,
        }

        private const string AlnumCharset = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ $%*+-./:";

        // Indexed [ecc][version]; index 0 is unused padding.
        private static readonly int[][] EccCodewordsPerBlock =
        {
            new[] { -1, 7, 10, 15, 20, 26, 18, 20, 24, 30, 18, 20, 24, 26, 30, 22, 24, 28, 30, 28, 28, 28, 28, 30, 30, 26, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30 },
            new[] { -1, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26, 30, 22, 22, 24, 24, 28, 28, 26, 26, 26, 26, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28 },
            new[] { -1, 13, 22, 18, 26, 18, 24, 18, 22, 20, 24, 28, 26, 24, 20, 30, 24, 28, 28, 26, 30, 28, 30, 30, 30, 30, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30 },
            new[] { -1, 17, 28, 22, 16, 22, 28, 26, 26, 24, 28, 24, 28, 22, 24, 24, 30, 28, 28, 26, 28, 30, 24, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30 },
        };
        private static readonly int[][] NumErrorCorrectionBlocks =
        {
            new[] { -1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 4, 4, 4, 4, 4, 6, 6, 6, 6, 7, 8, 8, 9, 9, 10, 12, 12, 12, 13, 14, 15, 16, 17, 18, 19, 19, 20, 21, 22, 24, 25 },
            new[] { -1, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5, 5, 8, 9, 9, 10, 10, 11, 13, 14, 16, 17, 17, 18, 20, 21, 23, 25, 26, 28, 29, 31, 33, 35, 37, 38, 40, 43, 45, 47, 49 },
            new[] { -1, 1, 1, 2, 2, 4, 4, 6, 6, 8, 8, 8, 10, 12, 16, 12, 17, 16, 18, 21, 20, 23, 23, 25, 27, 29, 34, 34, 35, 38, 40, 43, 45, 48, 51, 53, 56, 59, 62, 65, 68 },
            new[] { -1, 1, 1, 2, 4, 4, 4, 5, 6, 8, 8, 11, 11, 16, 16, 18, 16, 19, 21, 25, 25, 25, 34, 30, 32, 35, 37, 40, 42, 45, 48, 51, 54, 57, 60, 63, 66, 70, 74, 77, 81 },
        };
        // Format-info bits for each ECC level (L=01, M=00, Q=11, H=10).
        private static readonly int[] EccFormatBits = { 1, 0, 3, 2 };

        private const int PenaltyN1 = 3;
        private const int PenaltyN2 = 3;
        private const int PenaltyN3 = 40;
        private const int PenaltyN4 = 10;

        private static readonly int[] GfExp = new int[512];
        private static readonly int[] GfLog = new int[256];
        private static readonly Dictionary<int, int[]> Divisors = new Dictionary<int, int[]>();
        private static readonly Dictionary<int, Layout> Layouts = new Dictionary<int, Layout>();

        static PmcQr()
        {
            int x = 1;
            for (int i = 0; i < 255; i++)
            {
                GfExp[i] = x;
                GfLog[x] = i;
                x <<= 1;
                if ((x & 0x100) != 0)
                    x ^= 0x11D;
            }
            for (int i = 255; i < 512; i++)
                GfExp[i] = GfExp[i - 255];
        }

        /// <summary>
        /// Encodes <paramref name="text"/> with error correction level <paramref name="ecc"/>,
        /// choosing the smallest version that fits and the best mask.
        /// Returns the module grid (true = dark), or null if the text does not fit in version 40.
        /// </summary>
        public static bool[,] Encode(string text, Ecc ecc = Ecc.M)
        {
            var m = EncodeAdvanced(text, ecc);
            if (m == null)
                return null;
            var grid = new bool[m.Size, m.Size];
            for (int y = 0; y < m.Size; y++)
                for (int x = 0; x < m.Size; x++)
                    grid[y, x] = m.Modules[y * m.Size + x] != 0;
            return grid;
        }

        /// <summary>
        /// Like <see cref="Encode"/> but returns the full symbol (version, mask, mode) and gives
        /// control over the version range and mask. <paramref name="mode"/> may be "" (auto),
        /// "numeric", "alphanumeric" or "byte". A <paramref name="mask"/> of -1 selects the mask
        /// with the lowest penalty. Returns null if the text cannot be encoded within
        /// <paramref name="maxVersion"/> or the mode is invalid.
        /// </summary>
        public static PmcQrMatrix EncodeAdvanced(string text, Ecc ecc = Ecc.M, int minVersion = 1, int maxVersion = 40, int mask = -1, string mode = "")
        {
            if (text == null)
                return null;
            int e = Math.Clamp((int)ecc, 0, 3);
            minVersion = Math.Clamp(minVersion, 1, 40);
            maxVersion = Math.Clamp(maxVersion, minVersion, 40);
            if (mode == "")
                mode = PickMode(text);
            byte[] payload = null;
            int charCount;
            switch (mode)
            {
                case "numeric":
                    if (!IsNumeric(text))
                        return null;
                    charCount = text.Length;
                    break;
                case "alphanumeric":
                    if (!IsAlnum(text))
                        return null;
                    charCount = text.Length;
                    break;
                case "byte":
                    payload = Encoding.UTF8.GetBytes(text);
                    charCount = payload.Length;
                    break;
                default:
                    return null;
            }

            int version = -1;
            for (int v = minVersion; v <= maxVersion; v++)
            {
                int capBits = NumDataCodewords(v, e) * 8;
                int ccBits = CharCountBits(mode, v);
                int used = 4 + ccBits + PayloadBits(mode, charCount);
                if (charCount < (1 << ccBits) && used <= capBits)
                {
                    version = v;
                    break;
                }
            }
            if (version < 0)
                return null;

            // Build the bit stream.
            var bits = new List<byte>();
            int modeBits = mode == "numeric" ? 1 : mode == "alphanumeric" ? 2 : 4;
            AppendBits(bits, modeBits, 4);
            AppendBits(bits, charCount, CharCountBits(mode, version));
            switch (mode)
            {
                case "numeric":
                    for (int i = 0; i < charCount;)
                    {
                        int n = Math.Min(3, charCount - i);
                        AppendBits(bits, int.Parse(text.Substring(i, n)), n * 3 + 1);
                        i += n;
                    }
                    break;
                case "alphanumeric":
                    for (int i = 0; i < charCount;)
                    {
                        if (i + 1 < charCount)
                        {
                            AppendBits(bits, AlnumCharset.IndexOf(text[i]) * 45 + AlnumCharset.IndexOf(text[i + 1]), 11);
                            i += 2;
                        }
                        else
                        {
                            AppendBits(bits, AlnumCharset.IndexOf(text[i]), 6);
                            i += 1;
                        }
                    }
                    break;
                case "byte":
                    foreach (byte b in payload)
                        AppendBits(bits, b, 8);
                    break;
            }

            int capacityBits = NumDataCodewords(version, e) * 8;
            AppendBits(bits, 0, Math.Min(4, capacityBits - bits.Count));
            AppendBits(bits, 0, (8 - bits.Count % 8) % 8);
            var data = new List<byte>(capacityBits / 8);
            var packed = new byte[bits.Count >> 3];
            for (int i = 0; i < bits.Count; i++)
                if (bits[i] != 0)
                    packed[i >> 3] |= (byte)(0x80 >> (i & 7));
            data.AddRange(packed);
            int pad = 0xEC;
            while (data.Count * 8 < capacityBits)
            {
                data.Add((byte)pad);
                pad = pad == 0xEC ? 0x11 : 0xEC;
            }

            byte[] codewords = AddEccAndInterleave(data.ToArray(), version, e);

            var m = BuildMatrix(codewords, version, e, mask);
            m.Mode = mode;
            return m;
        }

        /// <summary>
        /// Encodes <paramref name="text"/> and renders it as a minimal PNG (8-bit RGB,
        /// black-on-white) with <paramref name="modulePx"/> pixels per module and a quiet zone of
        /// <paramref name="quiet"/> modules on each side. No engine dependencies.
        /// Returns null if the text cannot be encoded.
        /// </summary>
        public static byte[] EncodePng(string text, Ecc ecc, int modulePx, int quiet)
        {
            var m = EncodeAdvanced(text, ecc);
            return m == null ? null : EncodePng(m, modulePx, quiet);
        }

        /// <summary>
        /// Renders <paramref name="m"/> as a minimal PNG (8-bit RGB, black-on-white) with
        /// <paramref name="modulePx"/> pixels per module and a quiet zone of
        /// <paramref name="quiet"/> modules on each side. Returns null for a null matrix.
        /// </summary>
        public static byte[] EncodePng(PmcQrMatrix m, int modulePx, int quiet)
        {
            if (m == null)
                return null;
            modulePx = Math.Max(1, modulePx);
            quiet = Math.Max(0, quiet);
            int across = m.Size + quiet * 2;
            int px = across * modulePx;
            var raw = new byte[px * (px * 3 + 1)];
            int o = 0;
            for (int y = 0; y < px; y++)
            {
                raw[o++] = 0; // filter: none
                int my = y / modulePx - quiet;
                for (int x = 0; x < px; x++)
                {
                    byte v = m.GetModule(x / modulePx - quiet, my) ? (byte)0 : (byte)255;
                    raw[o++] = v;
                    raw[o++] = v;
                    raw[o++] = v;
                }
            }
            return WritePng(px, px, 2, ZlibCompress(raw));
        }

        // ------------------------------------------------------------------
        // Segment helpers

        private static bool IsNumeric(string text)
        {
            for (int i = 0; i < text.Length; i++)
                if (text[i] < '0' || text[i] > '9')
                    return false;
            return true;
        }

        private static bool IsAlnum(string text)
        {
            for (int i = 0; i < text.Length; i++)
                if (AlnumCharset.IndexOf(text[i]) < 0)
                    return false;
            return true;
        }

        private static string PickMode(string text)
        {
            if (text.Length > 0 && IsNumeric(text))
                return "numeric";
            if (text.Length > 0 && IsAlnum(text))
                return "alphanumeric";
            return "byte";
        }

        internal static int CharCountBits(string mode, int version)
        {
            int idx = version <= 9 ? 0 : version <= 26 ? 1 : 2;
            switch (mode)
            {
                case "numeric":
                    return new[] { 10, 12, 14 }[idx];
                case "alphanumeric":
                    return new[] { 9, 11, 13 }[idx];
            }
            return new[] { 8, 16, 16 }[idx];
        }

        internal static int PayloadBits(string mode, int count)
        {
            switch (mode)
            {
                case "numeric":
                    return count / 3 * 10 + new[] { 0, 4, 7 }[count % 3];
                case "alphanumeric":
                    return count / 2 * 11 + count % 2 * 6;
            }
            return count * 8;
        }

        private static void AppendBits(List<byte> bits, int value, int length)
        {
            for (int i = length - 1; i >= 0; i--)
                bits.Add((byte)((value >> i) & 1));
        }

        // ------------------------------------------------------------------
        // Capacity

        internal static int NumRawDataModules(int ver)
        {
            int result = (16 * ver + 128) * ver + 64;
            if (ver >= 2)
            {
                int numAlign = ver / 7 + 2;
                result -= (25 * numAlign - 10) * numAlign - 55;
                if (ver >= 7)
                    result -= 36;
            }
            return result;
        }

        internal static int NumDataCodewords(int ver, int ecc)
        {
            return NumRawDataModules(ver) / 8 - EccCodewordsPerBlock[ecc][ver] * NumErrorCorrectionBlocks[ecc][ver];
        }

        // ------------------------------------------------------------------
        // Reed-Solomon over GF(256), primitive polynomial 0x11D

        private static int GfMul(int a, int b)
        {
            if (a == 0 || b == 0)
                return 0;
            return GfExp[GfLog[a] + GfLog[b]];
        }

        private static int[] RsDivisor(int degree)
        {
            var result = new int[degree];
            result[degree - 1] = 1;
            int root = 1;
            for (int i = 0; i < degree; i++)
            {
                for (int j = 0; j < degree; j++)
                {
                    result[j] = GfMul(result[j], root);
                    if (j + 1 < degree)
                        result[j] ^= result[j + 1];
                }
                root = GfMul(root, 2);
            }
            return result;
        }

        private static byte[] RsRemainder(byte[] data, int[] divisor)
        {
            int n = divisor.Length;
            // Generator polynomial coefficients are never zero, so their logs are always defined.
            var divLog = new int[n];
            for (int i = 0; i < n; i++)
                divLog[i] = GfLog[divisor[i]];
            var result = new int[n];
            foreach (byte b in data)
            {
                int factor = b ^ result[0];
                Array.Copy(result, 1, result, 0, n - 1);
                result[n - 1] = 0;
                if (factor != 0)
                {
                    int flog = GfLog[factor];
                    for (int i = 0; i < n; i++)
                        result[i] ^= GfExp[divLog[i] + flog];
                }
            }
            var outBytes = new byte[n];
            for (int i = 0; i < n; i++)
                outBytes[i] = (byte)result[i];
            return outBytes;
        }

        private static byte[] AddEccAndInterleave(byte[] data, int ver, int ecc)
        {
            int numBlocks = NumErrorCorrectionBlocks[ecc][ver];
            int blockEccLen = EccCodewordsPerBlock[ecc][ver];
            int rawCodewords = NumRawDataModules(ver) / 8;
            int numShortBlocks = numBlocks - rawCodewords % numBlocks;
            int shortBlockLen = rawCodewords / numBlocks;

            if (!Divisors.TryGetValue(blockEccLen, out int[] divisor))
            {
                divisor = RsDivisor(blockEccLen);
                Divisors[blockEccLen] = divisor;
            }
            var dataBlocks = new List<byte[]>();
            var eccBlocks = new List<byte[]>();
            int k = 0;
            for (int i = 0; i < numBlocks; i++)
            {
                int datLen = shortBlockLen - blockEccLen + (i < numShortBlocks ? 0 : 1);
                var dat = Slice(data, k, k + datLen);
                k += datLen;
                dataBlocks.Add(dat);
                eccBlocks.Add(RsRemainder(dat, divisor));
            }

            var result = new List<byte>();
            int longLen = shortBlockLen - blockEccLen + (numShortBlocks < numBlocks ? 1 : 0);
            for (int i = 0; i < longLen; i++)
                for (int j = 0; j < numBlocks; j++)
                    if (i < dataBlocks[j].Length)
                        result.Add(dataBlocks[j][i]);
            for (int i = 0; i < blockEccLen; i++)
                for (int j = 0; j < numBlocks; j++)
                    result.Add(eccBlocks[j][i]);
            return result.ToArray();
        }

        // ------------------------------------------------------------------
        // Matrix construction
        //
        // Each candidate symbol is held twice: as rows and as columns (transposed). Both use a byte
        // layout of one separator byte (value 2) before every line plus one at the end, padded with 2s
        // to a multiple of 8. Masking then runs on 64-bit words, and the penalty rules run as byte
        // counts instead of per-module loops. Everything that depends only on the version (function
        // patterns, data placement order, mask patterns) is cached.

        private const byte Sep = 2;

        private sealed class Layout
        {
            public byte[] BaseR;
            public byte[] BaseC;
            public int[] OrderR;
            public int[] OrderC;
            public long[][] MasksR;
            public long[][] MasksC;
            public int[][] FormatPos; // [rIndex, cIndex, bit]
        }

        private static PmcQrMatrix BuildMatrix(byte[] codewords, int ver, int ecc, int forcedMask)
        {
            var lay = GetLayout(ver);
            int size = ver * 4 + 17;
            int w = size + 1;
            var r = (byte[])lay.BaseR.Clone();
            var c = (byte[])lay.BaseC.Clone();
            int[] orderR = lay.OrderR;
            int[] orderC = lay.OrderC;
            int total = Math.Min(codewords.Length * 8, orderR.Length);
            int i = 0;
            foreach (byte b in codewords)
            {
                for (int k = 0; k < 8; k++)
                {
                    if (i >= total)
                        break;
                    int bit = (b >> (7 - k)) & 1;
                    r[orderR[i]] = (byte)bit;
                    c[orderC[i]] = (byte)bit;
                    i++;
                }
            }

            long[] rw = BytesToWords(r);
            int best = forcedMask;
            byte[] bestR = null;
            if (best >= 0 && best <= 7)
            {
                bestR = WordsToBytes(XorWords(rw, lay.MasksR[best]));
                PutFormat(bestR, null, lay.FormatPos, ecc, best);
            }
            else
            {
                long[] cw = BytesToWords(c);
                int minPenalty = int.MaxValue;
                for (int msk = 0; msk < 8; msk++)
                {
                    byte[] mr = WordsToBytes(XorWords(rw, lay.MasksR[msk]));
                    byte[] mc = WordsToBytes(XorWords(cw, lay.MasksC[msk]));
                    PutFormat(mr, mc, lay.FormatPos, ecc, msk);
                    int p = Penalty(mr, mc, size);
                    if (p < minPenalty)
                    {
                        minPenalty = p;
                        best = msk;
                        bestR = mr;
                    }
                }
            }

            var outBytes = new byte[size * size];
            for (int y = 0; y < size; y++)
            {
                int start = y * w + 1;
                Array.Copy(bestR, start, outBytes, y * size, size);
            }
            return new PmcQrMatrix { Size = size, Version = ver, Ecc = (Ecc)ecc, Mask = best, Modules = outBytes };
        }

        private static Layout GetLayout(int ver)
        {
            if (Layouts.TryGetValue(ver, out Layout cached))
                return cached;
            int size = ver * 4 + 17;
            int w = size + 1;
            int n = size * w + 1;
            int padded = (n + 7) & ~7;

            var mods = new byte[size * size];
            var fm = new byte[size * size];
            DrawFunctionPatterns(mods, fm, size, ver);

            var baseR = new byte[padded];
            var baseC = new byte[padded];
            Array.Fill(baseR, Sep);
            Array.Fill(baseC, Sep);
            var freeR = new byte[padded];
            var freeC = new byte[padded];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    int ri = y * w + 1 + x;
                    int ci = x * w + 1 + y;
                    byte v = mods[y * size + x];
                    baseR[ri] = v;
                    baseC[ci] = v;
                    if (fm[y * size + x] == 0)
                    {
                        freeR[ri] = 1;
                        freeC[ci] = 1;
                    }
                }
            }

            // Data placement order: two-module-wide columns, zig-zagging from the bottom right.
            var orderR = new List<int>();
            var orderC = new List<int>();
            int right = size - 1;
            while (right >= 1)
            {
                if (right == 6)
                    right = 5;
                bool upward = ((right + 1) & 2) == 0;
                for (int vert = 0; vert < size; vert++)
                {
                    int y = upward ? size - 1 - vert : vert;
                    for (int j = 0; j < 2; j++)
                    {
                        int x = right - j;
                        if (fm[y * size + x] == 0)
                        {
                            orderR.Add(y * w + 1 + x);
                            orderC.Add(x * w + 1 + y);
                        }
                    }
                }
                right -= 2;
            }

            long[] freeRWords = BytesToWords(freeR);
            long[] freeCWords = BytesToWords(freeC);
            var masksR = new long[8][];
            var masksC = new long[8][];
            for (int msk = 0; msk < 8; msk++)
            {
                masksR[msk] = AndWords(MaskPattern(msk, size, padded, false), freeRWords);
                masksC[msk] = AndWords(MaskPattern(msk, size, padded, true), freeCWords);
            }

            var fmtPos = new List<int[]>();
            foreach (var p in FormatPositions(size))
                fmtPos.Add(new[] { p.Y * w + 1 + p.X, p.X * w + 1 + p.Y, p.Bit });

            var lay = new Layout
            {
                BaseR = baseR,
                BaseC = baseC,
                OrderR = orderR.ToArray(),
                OrderC = orderC.ToArray(),
                MasksR = masksR,
                MasksC = masksC,
                FormatPos = fmtPos.ToArray(),
            };
            Layouts[ver] = lay;
            return lay;
        }

        // Bytes with 1 wherever mask msk inverts a module (separators 0).
        private static long[] MaskPattern(int msk, int size, int padded, bool transposed)
        {
            var bytes = new byte[padded];
            int w = size + 1;
            for (int a = 0; a < size; a++)
            {
                for (int b = 0; b < size; b++)
                {
                    int x = transposed ? a : b;
                    int y = transposed ? b : a;
                    bool inv;
                    switch (msk)
                    {
                        case 0: inv = (x + y) % 2 == 0; break;
                        case 1: inv = y % 2 == 0; break;
                        case 2: inv = x % 3 == 0; break;
                        case 3: inv = (x + y) % 3 == 0; break;
                        case 4: inv = (x / 3 + y / 2) % 2 == 0; break;
                        case 5: inv = x * y % 2 + x * y % 3 == 0; break;
                        case 6: inv = (x * y % 2 + x * y % 3) % 2 == 0; break;
                        default: inv = ((x + y) % 2 + x * y % 3) % 2 == 0; break;
                    }
                    bytes[a * w + 1 + b] = inv ? (byte)1 : (byte)0;
                }
            }
            return BytesToWords(bytes);
        }

        // (x, y, bit index) for both copies of the 15 format bits.
        private struct FmtPos
        {
            public int X;
            public int Y;
            public int Bit;
        }

        private static List<FmtPos> FormatPositions(int size)
        {
            var outList = new List<FmtPos>();
            for (int i = 0; i < 6; i++)
                outList.Add(new FmtPos { X = 8, Y = i, Bit = i });
            outList.Add(new FmtPos { X = 8, Y = 7, Bit = 6 });
            outList.Add(new FmtPos { X = 8, Y = 8, Bit = 7 });
            outList.Add(new FmtPos { X = 7, Y = 8, Bit = 8 });
            for (int i = 9; i < 15; i++)
                outList.Add(new FmtPos { X = 14 - i, Y = 8, Bit = i });
            for (int i = 0; i < 8; i++)
                outList.Add(new FmtPos { X = size - 1 - i, Y = 8, Bit = i });
            for (int i = 8; i < 15; i++)
                outList.Add(new FmtPos { X = 8, Y = size - 15 + i, Bit = i });
            return outList;
        }

        private static int FormatBits(int ecc, int msk)
        {
            int data = (EccFormatBits[ecc] << 3) | msk;
            int rem = data;
            for (int i = 0; i < 10; i++)
                rem = (rem << 1) ^ ((rem >> 9) * 0x537);
            return ((data << 10) | rem) ^ 0x5412;
        }

        private static void PutFormat(byte[] mr, byte[] mc, int[][] fmtPos, int ecc, int msk)
        {
            int bits = FormatBits(ecc, msk);
            bool hasC = mc != null;
            foreach (int[] p in fmtPos)
            {
                int v = (bits >> p[2]) & 1;
                mr[p[0]] = (byte)v;
                if (hasC)
                    mc[p[1]] = (byte)v;
            }
        }

        private static long[] XorWords(long[] a, long[] b)
        {
            var outW = new long[a.Length];
            for (int i = 0; i < outW.Length; i++)
                outW[i] = a[i] ^ b[i];
            return outW;
        }

        private static long[] AndWords(long[] a, long[] b)
        {
            var outW = new long[a.Length];
            for (int i = 0; i < outW.Length; i++)
                outW[i] = a[i] & b[i];
            return outW;
        }

        private static long[] Pad8(byte[] a, int v)
        {
            int n = a.Length;
            int p = (8 - n % 8) % 8;
            var padded = new byte[n + p];
            Array.Copy(a, padded, n);
            for (int i = 0; i < p; i++)
                padded[n + i] = (byte)v;
            return BytesToWords(padded);
        }

        // Standard penalty rules N1 (runs), N2 (2x2 blocks), N3 (finder-like), N4 (dark balance).
        internal static int Penalty(byte[] mr, byte[] mc, int size)
        {
            int w = size + 1;
            int result = 0;
            int span = size * w - 4;
            foreach (byte[] arr in new[] { mr, mc })
            {
                // N1: a run of length L >= 5 scores L - 2 = 3 * (5-windows) - 2 * (6-windows).
                long[] a = Pad8(Slice(arr, 0, span), 3);
                long[] b = Pad8(Slice(arr, 1, span + 1), 4);
                long[] c = Pad8(Slice(arr, 2, span + 2), 5);
                long[] d = Pad8(Slice(arr, 3, span + 3), 6);
                long[] e = Pad8(Slice(arr, 4, span + 4), 7);
                long[] f = Pad8(Slice(arr, 5, span + 5), 8);
                for (int i = 0; i < a.Length; i++)
                {
                    long ai = a[i];
                    long acc = (ai ^ b[i]) | (ai ^ c[i]) | (ai ^ d[i]) | (ai ^ e[i]);
                    b[i] = acc;
                    c[i] = acc | (ai ^ f[i]);
                }
                result += 3 * CountZeroBytes(b) - 2 * CountZeroBytes(c);
                // N3: 1:1:3:1:1 with four light modules on one side (the border counts as light).
                var sb = new StringBuilder(arr.Length * 4);
                foreach (byte bb in arr)
                    sb.Append(bb == Sep ? "0000" : bb == 1 ? "1" : "0");
                string s = sb.ToString();
                result += (CountOccurrences(s, "000010111010") + CountOccurrences(s, "010111010000")) * PenaltyN3;
            }

            // N2: 2x2 blocks of one color.
            int span2 = (size - 1) * w;
            long[] p = Pad8(Slice(mr, 0, span2), 3);
            long[] q = Pad8(Slice(mr, 1, span2 + 1), 4);
            long[] r2 = Pad8(Slice(mr, w, w + span2), 5);
            long[] t = Pad8(Slice(mr, w + 1, w + 1 + span2), 6);
            for (int i = 0; i < p.Length; i++)
            {
                long pi = p[i];
                p[i] = (pi ^ q[i]) | (pi ^ r2[i]) | (pi ^ t[i]);
            }
            result += CountZeroBytes(p) * PenaltyN2;

            // N4: dark/light balance.
            int dark = 0;
            foreach (byte bb in mr)
                if (bb == 1)
                    dark++;
            int total = size * size;
            int k = (int)Math.Ceiling(Math.Abs(dark * 20 - total * 10) / (double)total) - 1;
            result += k * PenaltyN4;
            return result;
        }

        private static int CountZeroBytes(long[] words)
        {
            int n = 0;
            foreach (long word in words)
            {
                ulong u = (ulong)word;
                for (int j = 0; j < 8; j++)
                    if (((u >> (j * 8)) & 0xff) == 0)
                        n++;
            }
            return n;
        }

        // Non-overlapping count, matching Godot's String.count.
        private static int CountOccurrences(string s, string needle)
        {
            int count = 0;
            int pos = 0;
            while ((pos = s.IndexOf(needle, pos, StringComparison.Ordinal)) >= 0)
            {
                count++;
                pos += needle.Length;
            }
            return count;
        }

        private static void DrawFunctionPatterns(byte[] mods, byte[] funcMask, int size, int ver)
        {
            // Timing patterns.
            for (int i = 0; i < size; i++)
            {
                SetFunc(mods, funcMask, size, 6, i, i % 2 == 0);
                SetFunc(mods, funcMask, size, i, 6, i % 2 == 0);
            }
            // Finder patterns with separators.
            foreach (var center in new[] { (X: 3, Y: 3), (X: size - 4, Y: 3), (X: 3, Y: size - 4) })
            {
                for (int dy = -4; dy <= 4; dy++)
                {
                    for (int dx = -4; dx <= 4; dx++)
                    {
                        int xx = center.X + dx;
                        int yy = center.Y + dy;
                        if (xx >= 0 && xx < size && yy >= 0 && yy < size)
                        {
                            int dist = Math.Max(Math.Abs(dx), Math.Abs(dy));
                            SetFunc(mods, funcMask, size, xx, yy, dist != 2 && dist != 4);
                        }
                    }
                }
            }
            // Alignment patterns.
            int[] pos = AlignmentPositions(ver, size);
            int n = pos.Length;
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j < n; j++)
                {
                    if ((i == 0 && j == 0) || (i == 0 && j == n - 1) || (i == n - 1 && j == 0))
                        continue;
                    for (int dy = -2; dy <= 2; dy++)
                        for (int dx = -2; dx <= 2; dx++)
                            SetFunc(mods, funcMask, size, pos[i] + dx, pos[j] + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
                }
            }
            // Reserve the format areas (real bits are written per mask) and set the always-dark module.
            foreach (var fp in FormatPositions(size))
                SetFunc(mods, funcMask, size, fp.X, fp.Y, false);
            SetFunc(mods, funcMask, size, 8, size - 8, true);
            // Version information (versions 7+).
            if (ver >= 7)
            {
                int rem = ver;
                for (int i = 0; i < 12; i++)
                    rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
                int bits = (ver << 12) | rem;
                for (int i = 0; i < 18; i++)
                {
                    bool bit = ((bits >> i) & 1) != 0;
                    int a = size - 11 + i % 3;
                    int b = i / 3;
                    SetFunc(mods, funcMask, size, a, b, bit);
                    SetFunc(mods, funcMask, size, b, a, bit);
                }
            }
        }

        private static void SetFunc(byte[] mods, byte[] funcMask, int size, int x, int y, bool dark)
        {
            mods[y * size + x] = dark ? (byte)1 : (byte)0;
            funcMask[y * size + x] = 1;
        }

        private static int[] AlignmentPositions(int ver, int size)
        {
            if (ver == 1)
                return new int[0];
            int numAlign = ver / 7 + 2;
            int step = (ver * 8 + numAlign * 3 + 5) / (numAlign * 4 - 4) * 2;
            var result = new int[numAlign];
            result[0] = 6;
            int p = size - 7;
            for (int i = numAlign - 1; i > 0; i--)
            {
                result[i] = p;
                p -= step;
            }
            return result;
        }

        // ------------------------------------------------------------------
        // Packed-word helpers (PackedByteArray.to_int64_array equivalent: 8 bytes per word,
        // little-endian).

        private static long[] BytesToWords(byte[] bytes)
        {
            var words = new long[bytes.Length / 8];
            for (int i = 0; i < words.Length; i++)
            {
                long word = 0;
                for (int j = 0; j < 8; j++)
                    word |= (long)bytes[i * 8 + j] << (j * 8);
                words[i] = word;
            }
            return words;
        }

        private static byte[] WordsToBytes(long[] words)
        {
            var bytes = new byte[words.Length * 8];
            for (int i = 0; i < words.Length; i++)
            {
                ulong u = (ulong)words[i];
                for (int j = 0; j < 8; j++)
                    bytes[i * 8 + j] = (byte)(u >> (j * 8));
            }
            return bytes;
        }

        private static byte[] Slice(byte[] a, int begin, int end)
        {
            var outBytes = new byte[end - begin];
            Array.Copy(a, begin, outBytes, 0, outBytes.Length);
            return outBytes;
        }

        // ------------------------------------------------------------------
        // Minimal PNG writer (8-bit RGB) + zlib framing around a raw deflate stream.

        private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        private static byte[] WritePng(int width, int height, int colorType, byte[] zlibData)
        {
            using (var ms = new MemoryStream())
            {
                ms.Write(PngSignature, 0, PngSignature.Length);
                var ihdr = new byte[13];
                WriteU32BE(ihdr, 0, (uint)width);
                WriteU32BE(ihdr, 4, (uint)height);
                ihdr[8] = 8;                  // bit depth
                ihdr[9] = (byte)colorType;    // 2 = truecolor RGB
                // compression 0, filter 0, interlace 0 — already zeroed
                WriteChunk(ms, "IHDR", ihdr);
                WriteChunk(ms, "IDAT", zlibData);
                WriteChunk(ms, "IEND", new byte[0]);
                return ms.ToArray();
            }
        }

        private static void WriteChunk(Stream s, string type, byte[] data)
        {
            var header = new byte[4];
            WriteU32BE(header, 0, (uint)data.Length);
            s.Write(header, 0, 4);
            var typeBytes = Encoding.ASCII.GetBytes(type);
            s.Write(typeBytes, 0, 4);
            s.Write(data, 0, data.Length);
            uint crc = Crc32(typeBytes, data);
            var footer = new byte[4];
            WriteU32BE(footer, 0, crc);
            s.Write(footer, 0, 4);
        }

        private static void WriteU32BE(byte[] buf, int offset, uint v)
        {
            buf[offset] = (byte)(v >> 24);
            buf[offset + 1] = (byte)(v >> 16);
            buf[offset + 2] = (byte)(v >> 8);
            buf[offset + 3] = (byte)v;
        }

        // zlib stream (RFC 1950): 2-byte header + raw deflate body (RFC 1951) + big-endian Adler32.
        private static byte[] ZlibCompress(byte[] raw)
        {
            using (var ms = new MemoryStream())
            {
                ms.WriteByte(0x78);
                ms.WriteByte(0x9C);
                using (var ds = new DeflateStream(ms, CompressionLevel.Optimal, true))
                    ds.Write(raw, 0, raw.Length);
                uint adler = Adler32(raw);
                var tail = new byte[4];
                WriteU32BE(tail, 0, adler);
                ms.Write(tail, 0, 4);
                return ms.ToArray();
            }
        }

        private static uint Adler32(byte[] data)
        {
            const uint mod = 65521;
            uint a = 1;
            uint b = 0;
            foreach (byte bb in data)
            {
                a = (a + bb) % mod;
                b = (b + a) % mod;
            }
            return (b << 16) | a;
        }

        private static readonly uint[] CrcTable = BuildCrcTable();

        private static uint[] BuildCrcTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                table[i] = c;
            }
            return table;
        }

        private static uint Crc32(byte[] typeBytes, byte[] data)
        {
            uint crc = 0xFFFFFFFFu;
            foreach (byte b in typeBytes)
                crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
            foreach (byte b in data)
                crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return crc ^ 0xFFFFFFFFu;
        }
    }
}
