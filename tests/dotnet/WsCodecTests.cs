// Port of test_ws.gd::_codec — pure WebSocket frame/decoder unit tests, no host needed.

using System;
using System.Text;
using NUnit.Framework;
using Splatter.Pmc;

[TestFixture]
internal sealed class WsCodecTests {
    private static readonly byte[] Key = { 0x37, 0xfa, 0x21, 0x3d };

    [Test]
    public void AcceptKeyRfcExample() {
        Assert.That(PmcWsFrame.AcceptKey("dGhlIHNhbXBsZSBub25jZQ=="),
            Is.EqualTo("s3pPLMBiTxaQ9kYGzzhZRbK+xOo="));
    }

    [Test]
    public void MaskRoundTripsAcrossBoundarySizes() {
        foreach (int n in new[] { 0, 1, 7, 8, 9, 63, 64, 65, 125, 126, 127, 1000, 65535, 65536, 70001 }) {
            var data = new byte[n];
            for (int i = 0; i < n; i++) data[i] = (byte)((i * 31 + 7) % 256);
            var masked = PmcWsFrame.XorMask(data, Key);
            Assert.That(masked.Length, Is.EqualTo(n));
            for (int i = 0; i < n; i++) {
                Assert.That(masked[i], Is.EqualTo((byte)(data[i] ^ Key[i % 4])),
                    "xor_mask byte " + i + " of " + n);
            }
            Assert.That(PmcWsFrame.XorMask(masked, Key), Is.EqualTo(data),
                "xor_mask round-trips " + n + " bytes");

            var dec = new PmcWsDecoder { MaxMessageBytes = 1 << 20 };
            dec.Push(PmcWsFrame.Encode(PmcWsFrame.OpBinary, data, true, Key));
            var ev = dec.Next();
            Assert.That(ev, Is.Not.Null, "no event for " + n + " bytes");
            Assert.That(ev.Type, Is.EqualTo(PmcWsEvent.EvBinary));
            Assert.That(ev.Payload, Is.EqualTo(data), "decoded " + n + " bytes");

            int hdr = PmcWsFrame.Binary(data).Length - n;
            Assert.That(hdr, Is.EqualTo(n < 126 ? 2 : (n < 65536 ? 4 : 10)),
                "header length for " + n);
        }
    }

    [Test]
    public void UnmaskedTextRfcExample() {
        Assert.That(ToHex(PmcWsFrame.Text("Hello")), Is.EqualTo("810548656c6c6f"));
    }

    [Test]
    public void MaskedRfcExampleDecodes() {
        var d = new PmcWsDecoder();
        d.Push(new byte[] { 0x81, 0x85, 0x37, 0xfa, 0x21, 0x3d, 0x7f, 0x9f, 0x4d, 0x51, 0x58 });
        var ev = d.Next();
        Assert.That(ev.Type, Is.EqualTo(PmcWsEvent.EvText));
        Assert.That(ev.Text, Is.EqualTo("Hello"));
    }

    [Test]
    public void ByteAtATimeDecode() {
        var d = new PmcWsDecoder();
        var frame = PmcWsFrame.Encode(PmcWsFrame.OpText,
            Encoding.UTF8.GetBytes("héllo wörld"), true, Key);
        PmcWsEvent got = null;
        foreach (byte b in frame) {
            d.Push(new[] { b });
            var e = d.Next();
            if (e != null) got = e;
        }
        Assert.That(got, Is.Not.Null);
        Assert.That(got.Type, Is.EqualTo(PmcWsEvent.EvText));
        Assert.That(got.Text, Is.EqualTo("héllo wörld"));
    }

    [Test]
    public void ChunkedMaskedFrameUnmasksWithKeyRotation() {
        var payload = new byte[100003];
        for (int i = 0; i < payload.Length; i++) payload[i] = (byte)((i * 7 + 3) & 0xFF);
        var frame = PmcWsFrame.Encode(PmcWsFrame.OpBinary, payload, true, Key);
        var d = new PmcWsDecoder();
        PmcWsEvent got = null;
        int pos = 0;
        while (pos < frame.Length) {
            int step = pos % 2 == 0 ? 7001 : 3333;
            int n = Math.Min(step, frame.Length - pos);
            var chunk = new byte[n];
            Buffer.BlockCopy(frame, pos, chunk, 0, n);
            d.Push(chunk);
            pos += n;
            var e = d.Next();
            if (e != null) got = e;
        }
        Assert.That(got, Is.Not.Null);
        Assert.That(got.Type, Is.EqualTo(PmcWsEvent.EvBinary));
        Assert.That(got.Payload, Is.EqualTo(payload));
    }

    [Test]
    public void CloseReasonTruncatedTo125() {
        Assert.That(PmcWsFrame.Close(1000, new string('x', 200)).Length, Is.EqualTo(2 + 125));
    }

    [Test]
    public void CloseCodeValidation() {
        Assert.That(PmcWsFrame.IsValidCloseCode(4000), Is.True);
        Assert.That(PmcWsFrame.IsValidCloseCode(1005), Is.False);
        Assert.That(PmcWsFrame.IsValidCloseCode(999), Is.False);
    }

    [Test]
    public void DecoderEmitsProtocolErrors() {
        // Unmasked client frame.
        var d = new PmcWsDecoder();
        d.Push(PmcWsFrame.Text("x"));
        var ev = d.Next();
        Assert.That(ev.Type, Is.EqualTo(PmcWsEvent.EvError));
        Assert.That(ev.Code, Is.EqualTo(1002));

        // RSV bit without extension.
        d = new PmcWsDecoder();
        var rsv = PmcWsFrame.Encode(PmcWsFrame.OpText, new byte[] { (byte)'x' }, true, Key);
        rsv[0] |= 0x40;
        d.Push(rsv);
        ev = d.Next();
        Assert.That(ev.Code, Is.EqualTo(1002));

        // Continuation without a start.
        d = new PmcWsDecoder();
        d.Push(PmcWsFrame.Encode(0, new byte[] { (byte)'x' }, true, Key));
        Assert.That(d.Next().Code, Is.EqualTo(1002));

        // Invalid UTF-8 in a text frame.
        d = new PmcWsDecoder();
        d.Push(PmcWsFrame.Encode(1, new byte[] { 0xC3, 0x28 }, true, Key));
        ev = d.Next();
        Assert.That(ev.Code, Is.EqualTo(1007));

        // Declared 2 GiB payload rejected at header time.
        d = new PmcWsDecoder();
        var claim = new byte[] { 0x82, 0xFF, 0, 0, 0, 0, 0x80, 0, 0, 0, 1, 2, 3, 4 };
        d.Push(claim);
        ev = d.Next();
        Assert.That(ev.Code, Is.EqualTo(1009));
    }

    private static string ToHex(byte[] b) {
        var sb = new StringBuilder(b.Length * 2);
        foreach (byte x in b) sb.Append(x.ToString("x2"));
        return sb.ToString();
    }
}
