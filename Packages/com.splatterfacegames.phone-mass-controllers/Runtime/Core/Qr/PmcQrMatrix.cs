using System.Text;

namespace Splatter.Pmc
{
    /// <summary>
    /// A finished QR Code symbol: a square grid of dark/light modules.
    /// Produced by <see cref="PmcQr.EncodeAdvanced"/>. The grid does not include the quiet zone.
    /// </summary>
    public sealed class PmcQrMatrix
    {
        /// <summary>Width and height in modules (17 + 4 * version).</summary>
        public int Size;
        /// <summary>QR version, 1..40.</summary>
        public int Version;
        /// <summary>Error correction level used.</summary>
        public PmcQr.Ecc Ecc;
        /// <summary>Mask pattern applied, 0..7.</summary>
        public int Mask;
        /// <summary>Encoding mode used for the payload: "numeric", "alphanumeric" or "byte".</summary>
        public string Mode = "";
        /// <summary>Row-major module data, 1 = dark. Index is <c>y * Size + x</c>.</summary>
        public byte[] Modules = new byte[0];

        /// <summary>
        /// Returns true if the module at column <paramref name="x"/>, row <paramref name="y"/> is dark.
        /// Coordinates outside the symbol return false (light, like the quiet zone).
        /// </summary>
        public bool GetModule(int x, int y)
        {
            if (x < 0 || y < 0 || x >= Size || y >= Size)
                return false;
            return Modules[y * Size + x] != 0;
        }

        /// <summary>
        /// Returns the matrix as an array of strings, one per row, "1" for dark and "0" for light.
        /// Handy for debugging and for cross-checking against other encoders.
        /// </summary>
        public string[] ToRows()
        {
            var rows = new string[Size];
            for (int y = 0; y < Size; y++)
            {
                var line = new StringBuilder(Size);
                for (int x = 0; x < Size; x++)
                    line.Append(Modules[y * Size + x] != 0 ? '1' : '0');
                rows[y] = line.ToString();
            }
            return rows;
        }
    }
}
