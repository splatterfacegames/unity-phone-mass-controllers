// Port of test_ws.gd::_raw_protocol + _violations + pre-hello garbage — RFC 6455 edge cases
// against a live PmcHostCore.

using System;
using System.Text;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Splatter.Pmc;

[TestFixture]
internal sealed class WsViolationTests {
    private static readonly byte[] Key = { 1, 2, 3, 4 };

    private HostFixture _f;
    private int Port { get { return _f.Port; } }

    [SetUp]
    public void SetUp() {
        _f = HostFixture.Start(cfg => {
            cfg.HelloTimeoutSeconds = 0.5f;
            cfg.HeartbeatSeconds = 0.25f;
            cfg.MaxConnectionsPerAddress = 0;
            cfg.MessageReceived += (p, d) => {
                var bytes = d as byte[];
                if (bytes != null) _f.Host.Send(p, bytes);
                else _f.Host.Send(p, new JObject { ["echo"] = d as JToken ?? JValue.CreateNull() });
            };
        });
    }

    [TearDown]
    public void TearDown() {
        _f?.Dispose();
    }

    // Sends bytes (already past the upgrade) and expects the server close frame to carry `code`.
    private void ExpectClose(string label, byte[] bytes, int code, bool hello = true) {
        using (var ws = new TestWs(Port)) {
            ws.Upgrade();
            if (hello) {
                ws.Hello();
            }
            ws.SendRaw(bytes);
            Assert.That(ws.RecvClose(4000), Is.EqualTo(code), label);
            Assert.That(ws.WaitTcpClose(4000), Is.True, label + ": socket closed");
        }
    }

    [Test]
    public void ControlFramesAndFragmentation() {
        using (var ws = new TestWs(Port)) {
            ws.Upgrade();
            ws.Hello(new JObject { ["name"] = "raw" });

            // Ping echoes back as pong with the same payload.
            ws.Send(9, Encoding.UTF8.GetBytes("are you there"));
            var pong = ws.Recv();
            Assert.That(pong.Op, Is.EqualTo(10), "pong op");
            Assert.That(Encoding.UTF8.GetString(pong.Payload), Is.EqualTo("are you there"));

            // Fragmented text with a control frame between fragments (splits the UTF-8 ✓).
            var msg = Encoding.UTF8.GetBytes(
                new JObject { ["t"] = "msg", ["d"] = "fragmented ✓ message" }
                    .ToString(Newtonsoft.Json.Formatting.None));
            ws.Send(1, Sub(msg, 0, 5), fin: false);
            ws.Send(9, new byte[] { 1, 2 });
            ws.Send(0, Sub(msg, 5, 14), fin: false);
            ws.Send(0, Sub(msg, 19, msg.Length - 19), fin: true);
            var pong2 = ws.Recv();
            Assert.That(pong2.Op, Is.EqualTo(10), "ping between fragments answered");
            var echo = ws.RecvJson();
            Assert.That((string)echo["t"], Is.EqualTo("msg"));
            Assert.That((string)((JObject)echo["d"])["echo"], Is.EqualTo("fragmented ✓ message"));

            // Fragmented binary reassembled (4 x 50000).
            var bin = new byte[200000];
            for (int i = 0; i < bin.Length; i++) bin[i] = (byte)(i % 251);
            for (int i = 0; i < 4; i++) {
                ws.Send(i == 0 ? 2 : 0, Sub(bin, i * 50000, 50000), fin: i == 3);
            }
            var be = ws.Recv();
            Assert.That(be.Op, Is.EqualTo(2));
            Assert.That(be.Payload, Is.EqualTo(bin), "fragmented binary reassembled");

            // Clean close handshake: server echoes the code, then TCP closes.
            ws.Send(1, new byte[0]);
            ws.Send(8, Concat(new byte[] { 0x03, 0xE8 }, Encoding.UTF8.GetBytes("done")));
            Assert.That(ws.RecvClose(), Is.EqualTo(1000), "server echoes close code 1000");
            Assert.That(ws.WaitTcpClose(3000), Is.True, "server closes TCP after close handshake");
        }
    }

    [Test]
    public void UnmaskedFrameGets1002() {
        ExpectClose("unmasked frame -> 1002", PmcWsFrame.Text("{\"t\":\"msg\",\"d\":1}"), 1002);
    }

    [Test]
    public void RsvWithoutExtensionGets1002() {
        var rsv = PmcWsFrame.Encode(1, new byte[] { (byte)'x' }, true, Key);
        rsv[0] |= 0x40;
        ExpectClose("RSV1 without extension -> 1002", rsv, 1002);
    }

    [Test]
    public void UnknownOpcodeGets1002() {
        ExpectClose("unknown opcode -> 1002", PmcWsFrame.Encode(3, new byte[0], true, Key), 1002);
    }

    [Test]
    public void ContinuationWithoutStartGets1002() {
        ExpectClose("continuation without start -> 1002",
            PmcWsFrame.Encode(0, new byte[] { (byte)'x' }, true, Key), 1002);
    }

    [Test]
    public void NewMessageDuringFragmentationGets1002() {
        var both = Concat(
            PmcWsFrame.Encode(1, new byte[] { (byte)'a' }, false, Key),
            PmcWsFrame.Encode(1, new byte[] { (byte)'b' }, true, Key));
        ExpectClose("new message during fragmentation -> 1002", both, 1002);
    }

    [Test]
    public void OversizedControlFrameGets1002() {
        ExpectClose("control frame > 125 -> 1002",
            PmcWsFrame.Encode(9, new byte[126], true, Key), 1002);
    }

    [Test]
    public void FragmentedPingGets1002() {
        ExpectClose("fragmented ping -> 1002", PmcWsFrame.Encode(9, new byte[0], false, Key), 1002);
    }

    [Test]
    public void InvalidUtf8Gets1007() {
        ExpectClose("invalid UTF-8 text -> 1007",
            PmcWsFrame.Encode(1, new byte[] { 0xC3, 0x28 }, true, Key), 1007);
    }

    [Test]
    public void Declared2GibGets1009() {
        var claim = Concat(new byte[] { 0x82, 0xFF, 0, 0, 0, 0, 0x80, 0, 0, 0 }, Key);
        ExpectClose("declared 2 GiB -> 1009", claim, 1009);
    }

    [Test]
    public void FragmentsOverMaxGet1009() {
        var half = new byte[600000];
        var frag = Concat(
            PmcWsFrame.Encode(2, half, false, Key),
            PmcWsFrame.Encode(0, half, true, Key));
        ExpectClose("fragments over max -> 1009", frag, 1009);
    }

    [Test]
    public void CloseCode999Gets1002() {
        ExpectClose("close code 999 -> 1002",
            PmcWsFrame.Encode(8, new byte[] { 0x03, 0xE7 }, true, Key), 1002);
    }

    [Test]
    public void CloseOneBytePayloadGets1002() {
        ExpectClose("close with 1-byte payload -> 1002",
            PmcWsFrame.Encode(8, new byte[] { 3 }, true, Key), 1002);
    }

    [Test]
    public void CloseReservedCode1005Gets1002() {
        ExpectClose("close with reserved code 1005 -> 1002",
            PmcWsFrame.Encode(8, new byte[] { 0x03, 0xED }, true, Key), 1002);
    }

    [Test]
    public void EmptyCloseEchoesEmpty() {
        ExpectClose("empty close -> empty close", PmcWsFrame.Encode(8, new byte[0], true, Key), 1005);
    }

    [Test]
    public void BinaryBeforeHelloRejectsBadHello() {
        using (var ws = new TestWs(Port)) {
            ws.Upgrade();
            ws.Send(2, new byte[] { 1, 2, 3 });
            var rej = ws.RecvJson();
            Assert.That((string)rej["code"], Is.EqualTo("bad_hello"), "binary before hello -> reject bad_hello");
            Assert.That(ws.RecvClose(), Is.EqualTo(4000), "reject closes with 4000");
        }
    }

    [Test]
    public void NonJsonFirstFrameRejectsBadHello() {
        using (var ws = new TestWs(Port)) {
            ws.Upgrade();
            ws.Send(1, Encoding.UTF8.GetBytes("not json"));
            var rej = ws.RecvJson();
            Assert.That((string)rej["code"], Is.EqualTo("bad_hello"), "non-JSON first frame -> bad_hello");
        }
    }

    [Test]
    public void MissingHelloTimesOut() {
        using (var ws = new TestWs(Port)) {
            ws.Upgrade();
            var rej = ws.RecvJson(4000);
            Assert.That((string)rej["code"], Is.EqualTo("bad_hello"),
                "no hello within hello_timeout -> bad_hello");
        }
    }

    [Test]
    public void RandomGarbageFramesCloseTheSocket() {
        var rng = new Random(99);
        for (int round = 0; round < 5; round++) {
            var junk = new byte[4096];
            rng.NextBytes(junk);
            using (var ws = new TestWs(Port)) {
                ws.Upgrade();
                ws.SendRaw(junk);
                Assert.That(ws.WaitTcpClose(5000), Is.True,
                    "random garbage frames round " + round + " -> socket closed");
            }
        }
    }

    [Test]
    public void PartialFramesThenDisconnectHostSurvives() {
        using (var ws = new TestWs(Port)) {
            ws.Upgrade();
            ws.SendRaw(new byte[] { 0x81, 0xFE, 0x10 }); // truncated extended length
        }
        using (var s = new TestHttp(Port)) {
            s.Send("GET /pmc/ws HTTP/1.1\r\nUpgrade: websocket\r\n");
        }
        System.Threading.Thread.Sleep(250);
        Assert.That(_f.Host.Running, Is.True, "partial frames / headers then disconnect: host fine");
        using (var ws2 = new TestWs(Port)) {
            ws2.Upgrade();
            ws2.Hello();
        }
    }

    private static byte[] Sub(byte[] src, int off, int len) {
        var d = new byte[len];
        Buffer.BlockCopy(src, off, d, 0, len);
        return d;
    }

    private static byte[] Concat(byte[] a, byte[] b) {
        var d = new byte[a.Length + b.Length];
        Buffer.BlockCopy(a, 0, d, 0, a.Length);
        Buffer.BlockCopy(b, 0, d, a.Length, b.Length);
        return d;
    }
}
