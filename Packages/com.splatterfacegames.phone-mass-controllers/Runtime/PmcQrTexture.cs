using UnityEngine;

namespace Splatter.Pmc
{
    /// <summary>
    /// Turns a <see cref="PmcQrMatrix"/> into a <see cref="Texture2D"/> — dark modules on white,
    /// point-filtered, ready for a RawImage. Used by <see cref="PmcHost.QrTexture"/>; usable
    /// standalone for any payload.
    /// </summary>
    public static class PmcQrTexture
    {
        /// <summary>Quiet zone width in modules. 4 is the QR standard; scanners need it.</summary>
        public const int DefaultQuietModules = 4;

        /// <summary>Encodes <paramref name="text"/> as a QR texture of <paramref name="modulePx"/> px per module.</summary>
        public static Texture2D Encode(string text, int modulePx = 8, int quietModules = DefaultQuietModules, PmcQr.Ecc ecc = PmcQr.Ecc.M)
        {
            PmcQrMatrix m = PmcQr.Encode(text, ecc);
            if (m == null)
                return null;
            return FromMatrix(m, modulePx, quietModules);
        }

        /// <summary>
        /// Builds a texture from an encoded QR symbol. Size is (Size + 2·quiet) ·
        /// <paramref name="modulePx"/> px per side. Matrix row y lands on the texture's top
        /// scanline so the code isn't mirrored when displayed.
        /// </summary>
        public static Texture2D FromMatrix(PmcQrMatrix matrix, int modulePx = 8, int quietModules = DefaultQuietModules)
        {
            if (matrix == null)
                return null;
            PmcQrMatrix m = matrix;
            return Build(m.Size, delegate (int x, int y) { return m[x, y]; }, modulePx, quietModules);
        }

        /// <summary>
        /// Builds a texture from a raw module matrix (<c>true</c> = dark), indexed [row, column].
        /// Convenience for tests and callers that already hold a bool grid.
        /// </summary>
        public static Texture2D FromMatrix(bool[,] modules, int modulePx = 8, int quietModules = DefaultQuietModules)
        {
            if (modules == null)
                return null;
            int n = modules.GetLength(0);
            if (n == 0 || modules.GetLength(1) != n)
                return null;
            bool[,] grid = modules;
            return Build(n, delegate (int x, int y) { return grid[y, x]; }, modulePx, quietModules);
        }

        private static Texture2D Build(int n, System.Func<int, int, bool> isDark, int modulePx, int quietModules)
        {
            if (n <= 0 || modulePx < 1 || quietModules < 0)
                return null;
            int total = n + quietModules * 2;
            int size = total * modulePx;

            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var px = new Color32[size * size];
            var white = new Color32(255, 255, 255, 255);
            var black = new Color32(0, 0, 0, 255);
            for (int i = 0; i < px.Length; i++)
                px[i] = white;

            for (int y = 0; y < n; y++)
            {
                // Texture2D pixel row 0 is the bottom scanline; write matrix row y at the top.
                int baseRow = (total - 1 - (y + quietModules)) * modulePx;
                int left = quietModules * modulePx;
                for (int x = 0; x < n; x++)
                {
                    if (!isDark(x, y))
                        continue;
                    int mx = left + x * modulePx;
                    for (int dy = 0; dy < modulePx; dy++)
                    {
                        int row = (baseRow + dy) * size + mx;
                        for (int dx = 0; dx < modulePx; dx++)
                            px[row + dx] = black;
                    }
                }
            }

            tex.SetPixels32(px);
            tex.Apply();
            tex.filterMode = FilterMode.Point;
            tex.name = "pmc-qr";
            return tex;
        }
    }
}
