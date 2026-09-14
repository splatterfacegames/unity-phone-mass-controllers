using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using Splatter.Pmc;

// Dumps QR matrices for a verification corpus so tests/node/qr-decode.mjs can decode them
// with independent implementations (jsQR, ZXing, and module-by-module against the `qrcode`
// npm package). Port of the Godot tests/node/qr-dump.gd — same corpus, same output shape.
//
// Usage: dotnet run -c Release --project tests/qr-dump -- <out_dir>
internal static class Program
{
    private static readonly string[] Levels = { "L", "M", "Q", "H" };

    private static int Main(string[] args)
    {
        string outDir = args.Length > 0 ? args[0] : Path.Combine("tests", "node", "out", "qr");
        Directory.CreateDirectory(outDir);

        var entries = new List<Dictionary<string, object>>();
        var fixedTexts = new[]
        {
            "http://192.168.1.87:8086/?code=ABCD",
            "https://random-words-here.trycloudflare.com/?code=WXYZ",
            "http://10.0.0.2:8080/",
            "https://example.com",
            "https://example.com/a/very/long/path/that/keeps/going/and/going?with=query&params=true&and=more#fragment-too",
            "A",
            "",
            "HELLO WORLD",
            "HTTP://192.168.1.87:8086/",
            "0123456789",
            "31415926535897932384626433832795028841971693993751058209749445923",
            "Grüße aus Köln — ünïcödé ✓",
            "日本語のテキストとQRコード",
            "emoji 🎮🕹️📱 party",
            "line1\nline2\ttab",
        };
        long t0 = Stopwatch.GetTimestamp();
        foreach (string text in fixedTexts)
            for (int ecc = 0; ecc < 4; ecc++)
                Add(entries, "fixed", text, PmcQr.EncodeAdvanced(text, (PmcQr.Ecc)ecc));
        // Every version 1..40 at every level, filled close to capacity with byte / alphanumeric / numeric data.
        for (int v = 1; v <= 40; v++)
        {
            for (int ecc = 0; ecc < 4; ecc++)
            {
                string byteText = FillBytes(v, ecc);
                Add(entries, $"v{v}-{Levels[ecc]}-byte", byteText, PmcQr.EncodeAdvanced(byteText, (PmcQr.Ecc)ecc, v, v));
                if (v <= 20 || v % 5 == 0)
                {
                    string alnum = FillMode(v, ecc, "alphanumeric", "HTTP://PMC.EXAMPLE/$%*+-./: 0123456789");
                    Add(entries, $"v{v}-{Levels[ecc]}-alnum", alnum, PmcQr.EncodeAdvanced(alnum, (PmcQr.Ecc)ecc, v, v, -1, "alphanumeric"));
                    string num = FillMode(v, ecc, "numeric", "8675309");
                    Add(entries, $"v{v}-{Levels[ecc]}-num", num, PmcQr.EncodeAdvanced(num, (PmcQr.Ecc)ecc, v, v, -1, "numeric"));
                }
            }
        }
        // Every forced mask.
        for (int msk = 0; msk < 8; msk++)
        {
            const string url = "http://192.168.1.87:8086/?code=ABCD";
            Add(entries, $"mask{msk}", url, PmcQr.EncodeAdvanced(url, PmcQr.Ecc.M, 1, 40, msk));
        }
        double encodeMs = ElapsedMs(t0);

        // Timing for a typical URL.
        const int iters = 50;
        long t1 = Stopwatch.GetTimestamp();
        for (int i = 0; i < iters; i++)
            PmcQr.EncodeAdvanced("http://192.168.1.87:8086/?code=ABCD", PmcQr.Ecc.M);
        double typicalMs = ElapsedMs(t1) / iters;
        long t2 = Stopwatch.GetTimestamp();
        for (int i = 0; i < iters; i++)
            PmcQr.EncodeAdvanced("https://random-words-here.trycloudflare.com/?code=WXYZ", PmcQr.Ecc.M);
        double tunnelMs = ElapsedMs(t2) / iters;

        // PNG renders for a handful of entries (checks EncodePng end to end).
        int pngs = 0;
        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            string name = (string)e["name"];
            if (name == "fixed" || name.StartsWith("v10-") || name.StartsWith("v20-") || name.StartsWith("v40-M"))
            {
                var m = PmcQr.EncodeAdvanced((string)e["text"], (PmcQr.Ecc)(int)e["ecc"],
                    (int)e["version"], (int)e["version"], (int)e["mask"], (string)e["mode"]);
                byte[] png = PmcQr.EncodePng(m, 4, 4);
                string file = $"{i:D4}.png";
                File.WriteAllBytes(Path.Combine(outDir, file), png);
                e["png"] = file;
                e["png_module_px"] = 4;
                pngs++;
            }
        }

        var corpus = new Dictionary<string, object>
        {
            { "entries", entries },
            { "encode_total_ms", encodeMs },
            { "typical_url_ms", typicalMs },
            { "tunnel_url_ms", tunnelMs },
        };
        File.WriteAllText(Path.Combine(outDir, "corpus.json"), JsonSerializer.Serialize(corpus));
        Console.WriteLine($"qr-dump: {entries.Count} entries ({pngs} png) -> {outDir}; typical URL encode {typicalMs:F2} ms, tunnel URL {tunnelMs:F2} ms");
        return 0;
    }

    private static void Add(List<Dictionary<string, object>> entries, string name, string text, PmcQrMatrix m)
    {
        if (m == null)
        {
            Console.Error.WriteLine($"qr-dump: failed to encode {name}");
            Environment.Exit(1);
            return;
        }
        entries.Add(new Dictionary<string, object>
        {
            { "name", name },
            { "text", text },
            { "ecc", (int)m.Ecc },
            { "level", Levels[(int)m.Ecc] },
            { "version", m.Version },
            { "mask", m.Mask },
            { "mode", m.Mode },
            { "size", m.Size },
            { "rows", m.ToRows() },
        });
    }

    private static double ElapsedMs(long t0)
    {
        return (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
    }

    // Deterministic mixed ASCII/UTF-8 text sized to the byte capacity of version v (but too big
    // for v-1 where possible).
    private static string FillBytes(int v, int ecc)
    {
        int cap = ByteCapacity(v, ecc);
        var rng = new Random(v * 7 + ecc);
        var s = new StringBuilder();
        var pieces = new[] { "https://", "example", ".com/", "?q=", "é", "ü", "漢", "-", "_", "0", "Z", "~", "%20" };
        while (Encoding.UTF8.GetByteCount(s.ToString()) < cap)
        {
            string p = pieces[rng.Next(pieces.Length)];
            if (Encoding.UTF8.GetByteCount(s.ToString() + p) > cap)
                s.Append('x');
            else
                s.Append(p);
        }
        return s.ToString();
    }

    private static string FillMode(int v, int ecc, string mode, string alphabet)
    {
        int n = MaxCount(v, ecc, mode);
        var s = new StringBuilder();
        for (int i = 0; i < n; i++)
            s.Append(alphabet[i % alphabet.Length]);
        return s.ToString();
    }

    private static int ByteCapacity(int v, int ecc)
    {
        return MaxCount(v, ecc, "byte");
    }

    // Largest character count of the given mode that fits in version v (uses encoder internals).
    private static int MaxCount(int v, int ecc, string mode)
    {
        int capBits = PmcQr.NumDataCodewords(v, ecc) * 8;
        int n = 0;
        while (4 + PmcQr.CharCountBits(mode, v) + PmcQr.PayloadBits(mode, n + 1) <= capBits)
            n++;
        return n;
    }
}
