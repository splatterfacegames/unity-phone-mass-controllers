// Live host tests for the WebSocket path: real PmcHostCore on 127.0.0.1, raw client.

using System;
using System.Text;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Splatter.Pmc;

[TestFixture]
internal sealed class WsLiveTests {
    [Test]
    public void HelloWelcomeAndEcho() {
        using (var f = HostFixture.Start())
        using (var ws = new TestWs(f.Port)) {
            ws.Upgrade();
            var w = ws.Hello(new JObject { ["name"] = "nunit" });
            Assert.That((string)w["name"], Is.EqualTo("nunit"));
            Assert.That(((string)w["token"]).Length, Is.EqualTo(32));

            // Message echo path via a subscribed handler is tested by the host suites; here we
            // just verify the wire survives a round trip.
            ws.SendText(new JObject { ["t"] = "pmc.ping", ["c"] = 42 }.ToString());
            var pong = ws.RecvJson();
            Assert.That((string)pong["t"], Is.EqualTo("pmc.pong"));
            Assert.That((long)pong["c"], Is.EqualTo(42));
            Assert.That((long)pong["s"], Is.GreaterThan(0));
        }
    }

    [Test]
    public void FragmentedTextReassembles() {
        HostFixture f = null;
        f = HostFixture.Start(cfg => { cfg.MessageReceived += (p, d) => Echo(f.Host, p, d); });
        using (f)
        using (var ws = new TestWs(f.Port)) {
            ws.Upgrade();
            ws.Hello();
            var msg = Encoding.UTF8.GetBytes(new JObject {
                ["t"] = "msg",
                ["d"] = "frag ✓ mented",
            }.ToString());
            int cut = 8;
            ws.Send(1, Sub(msg, 0, 4), fin: false);
            ws.Send(9, Encoding.UTF8.GetBytes("mid")); // control frame mid-fragment is legal
            ws.Send(0, Sub(msg, 4, cut - 4), fin: false);
            ws.Send(0, Sub(msg, cut, msg.Length - cut), fin: true);
            var pong = ws.Recv();
            Assert.That(pong.Op, Is.EqualTo(10), "pong for the mid-fragment ping");
            Assert.That(Encoding.UTF8.GetString(pong.Payload), Is.EqualTo("mid"));
            var m = ws.RecvJson();
            Assert.That((string)m["t"], Is.EqualTo("msg"));
            Assert.That((string)((JObject)m["d"])["echo"], Is.EqualTo("frag ✓ mented"));
        }
    }

    [Test]
    public void BinaryEchoAndLargeMessage() {
        HostFixture f = null;
        f = HostFixture.Start(cfg => { cfg.MessageReceived += (p, d) => Echo(f.Host, p, d); });
        using (f)
        using (var ws = new TestWs(f.Port)) {
            ws.Upgrade();
            ws.Hello();
            var rng = new Random(1234);
            var bin = new byte[777];
            rng.NextBytes(bin);
            ws.Send(2, bin);
            var back = ws.Recv();
            Assert.That(back.Op, Is.EqualTo(2));
            Assert.That(back.Payload, Is.EqualTo(bin));

            // 1 MiB binary, one frame.
            var big = new byte[1 << 20];
            rng.NextBytes(big);
            ws.Send(2, big);
            var bigBack = ws.Recv(30000);
            Assert.That(bigBack.Op, Is.EqualTo(2));
            Assert.That(bigBack.Payload.Length, Is.EqualTo(big.Length));
            Assert.That(bigBack.Payload, Is.EqualTo(big));
        }
    }

    // pmc_test_server.gd: binary echoed raw, msg echoed as {"echo": d}.
    private static void Echo(PmcHostCore host, PmcPlayer p, object d) {
        var bytes = d as byte[];
        if (bytes != null) {
            host.Send(p, bytes);
            return;
        }
        host.Send(p, new JObject { ["echo"] = d as JToken ?? JValue.CreateNull() });
    }

    private static byte[] Sub(byte[] src, int off, int len) {
        var d = new byte[len];
        Buffer.BlockCopy(src, off, d, 0, len);
        return d;
    }
}
