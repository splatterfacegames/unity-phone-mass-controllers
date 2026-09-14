// Port of test_host_hint.gd, test_host_port.gd and the _urls/_lan/_qr_tunnel sections of
// test_host.gd — plus join URL building and info.json.

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Splatter.Pmc;

[TestFixture]
internal sealed class HostMiscTests {
    [Test]
    public void NoJoinsHintFiresOnce() {
        int hints = 0;
        using (var f = HostFixture.Start(cfg => {
            cfg.HeartbeatSeconds = 0f;
            cfg.NoJoinsHintSeconds = 0.3f;
            cfg.NoJoinsHint += () => hints++;
        })) {
            Assert.That(f.Until(() => hints == 1, 3000), Is.True, "hint fired after the deadline");
            System.Threading.Thread.Sleep(400);
            Assert.That(hints, Is.EqualTo(1), "hint fires only once");
        }
    }

    [Test]
    public void JoinSilencesHintAndUrlChangeRearms() {
        int hints = 0;
        using (var f = HostFixture.Start(cfg => {
            cfg.HeartbeatSeconds = 0f;
            cfg.NoJoinsHintSeconds = 0.4f;
            cfg.NoJoinsHint += () => hints++;
        })) {
            using (var ws = new TestWs(f.Port)) {
                ws.Upgrade();
                ws.Hello();
            }
            System.Threading.Thread.Sleep(800);
            Assert.That(hints, Is.EqualTo(0), "no hint when a phone joined");
            f.Host.AdvertiseUrl = "http://192.0.2.1:9000/";
            Assert.That(f.Until(() => hints == 1, 3000), Is.True,
                "re-armed hint fires when no new joins follow");
        }
    }

    [Test]
    public void HintOffByDefault() {
        int hints = 0;
        using (var f = HostFixture.Start(cfg => {
            cfg.HeartbeatSeconds = 0f;
            cfg.NoJoinsHint += () => hints++;
        })) {
            System.Threading.Thread.Sleep(500);
            Assert.That(hints, Is.EqualTo(0), "no hint with the default 0");
        }
    }

    [Test]
    public void PortSearchSkipsIpv4OnlyOccupant() {
        int basePort = FreePort();
        var blocker = new TcpListener(IPAddress.Any, basePort); // IPv4-only
        blocker.Start();
        try {
            using (var f = HostFixture.Start(cfg => {
                cfg.BindAddress = "*";
                cfg.Port = basePort;
                cfg.PortSearch = 5;
            })) {
                Assert.That(f.Port, Is.Not.EqualTo(basePort), "skipped the occupied port");
                Assert.That(f.Port, Is.InRange(basePort + 1, basePort + 5), "picked a port in range");
            }
        } finally {
            blocker.Stop();
        }
        using (var f2 = HostFixture.Start(cfg => { cfg.Port = basePort; })) {
            Assert.That(f2.Port, Is.EqualTo(basePort), "same port once free");
        }
    }

    [Test]
    public void PortSearchFindsNextAndZeroFails() {
        using (var f = HostFixture.Start()) {
            int port = f.Port;
            using (var f2 = HostFixture.Start(cfg => {
                cfg.Port = port;
                cfg.PortSearch = 5;
            })) {
                Assert.That(f2.Port, Is.GreaterThan(port).And.LessThanOrEqualTo(port + 5),
                    "next port used");
            }
            var h3 = new PmcHostCore { Port = port, PortSearch = 0, ControllerDir = "" };
            Assert.That(h3.Start(), Is.Not.EqualTo(0), "port_search 0 on a busy port fails");
            Assert.That(h3.Running, Is.False);
            Assert.That(h3.BoundPort, Is.EqualTo(port),
                "get_port returns configured port when not running");
        }
    }

    private static int FreePort() {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    [Test]
    public void JoinUrlBuilding() {
        using (var f = HostFixture.Start()) {
            var urls = new List<string>();
            f.Host.JoinUrlChanged += u => urls.Add(u);
            var lan = f.Host.LanAddresses();
            string expectHost = lan.Count > 0 ? lan[0] : "127.0.0.1";
            int port = f.Port;
            Assert.That(f.Host.JoinUrl(), Is.EqualTo("http://" + expectHost + ":" + port + "/"),
                "default LAN join URL");
            f.Host.JoinCode = "WXYZ";
            Assert.That(f.Host.JoinUrl(),
                Is.EqualTo("http://" + expectHost + ":" + port + "/?code=WXYZ"), "join code appended");
            f.Host.AdvertiseUrl = "https://play.example.com";
            Assert.That(f.Host.JoinUrl(), Is.EqualTo("https://play.example.com/?code=WXYZ"),
                "advertise_url gets trailing slash");
            f.Host.AdvertiseUrl = "https://play.example.com/room?x=1";
            Assert.That(f.Host.JoinUrl(), Is.EqualTo("https://play.example.com/room?x=1&code=WXYZ"),
                "code appended with &");
            f.Host.AdvertiseUrl = "10.1.2.3:9000";
            f.Host.JoinCode = "";
            Assert.That(f.Host.JoinUrl(), Is.EqualTo("http://10.1.2.3:9000/"), "scheme added");
            f.Host.AdvertiseUrl = "";
            Assert.That(urls.Count, Is.EqualTo(6), "join_url_changed emitted on each change");
            Assert.That(urls[urls.Count - 1], Is.EqualTo(f.Host.JoinUrl()),
                "signal carries the new URL");
        }
    }

    [Test]
    public void LanScoring() {
        using (var f = HostFixture.Start()) {
            var addrs = f.Host.LanAddresses();
            foreach (var a in addrs) {
                Assert.That(a.StartsWith("127.", StringComparison.Ordinal), Is.False, "no loopback");
                Assert.That(a.Contains(":"), Is.False, "IPv4 only");
            }
            var pairs = new[] {
                new[] { "Ethernet", "192.168.1.20", "vEthernet (WSL)", "172.28.160.1" },
                new[] { "Wi-Fi", "10.0.0.12", "vEthernet (Default Switch)", "172.17.32.1" },
                new[] { "Wi-Fi", "192.168.0.7", "Tailscale", "100.101.102.103" },
                new[] { "Ethernet 2", "192.168.1.5", "ZeroTier One [8056c2e21c000001]", "10.147.17.5" },
                new[] { "Wi-Fi", "192.168.1.5", "VirtualBox Host-Only Network", "192.168.56.1" },
                new[] { "eth0", "10.0.0.5", "docker0", "172.17.0.1" },
                new[] { "wlan0", "192.168.43.2", "br-1a2b3c", "172.18.0.1" },
                new[] { "en0", "192.168.1.30", "utun3", "10.8.0.2" },
                new[] { "enp3s0", "192.168.1.30", "wg0", "10.66.66.2" },
                new[] { "Ethernet", "192.168.1.20", "Ethernet", "169.254.10.10" },
                new[] { "Local Area Connection", "192.168.1.9", "VMware Network Adapter VMnet8", "192.168.150.1" },
                new[] { "Wi-Fi", "192.168.1.9", "vEthernet (Hyper-V Virtual Ethernet Adapter)", "192.168.1.200" },
                new[] { "Ethernet", "10.20.30.40", "Ethernet", "8.8.4.4" },
            };
            foreach (var pr in pairs) {
                int good = PmcHostCore.ScoreAddress(pr[0], pr[1]);
                int bad = PmcHostCore.ScoreAddress(pr[2], pr[3]);
                Assert.That(good, Is.GreaterThan(bad),
                    pr[0] + " " + pr[1] + " (" + good + ") beats " + pr[2] + " " + pr[3] + " (" + bad + ")");
            }
            Assert.That(PmcHostCore.ScoreAddress("lo", "127.0.0.1"), Is.EqualTo(-1000), "loopback excluded");
            Assert.That(PmcHostCore.ScoreAddress("eth0", "fe80::1"), Is.EqualTo(-1000), "IPv6 excluded");
        }
    }

    [Test]
    public void InfoJsonMatchesJoinUrl() {
        using (var f = HostFixture.Start()) {
            using (var s = new TestHttp(f.Port)) {
                var r = s.Exchange("GET /pmc/info.json HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n");
                Assert.That(r.Status, Is.EqualTo(200));
                var info = JObject.Parse(r.BodyText());
                Assert.That((string)info["join_url"], Is.EqualTo(f.Host.JoinUrl()), "info.json join_url");
            }
        }
    }

    // ---- tunnel wiring (fake tunnel driven through reflection; port of _qr_tunnel) ----

    [Test]
    public void TunnelReadyAdvertisesAndGeneratesCode() {
        var states = new List<string[]>();
        var urls = new List<string>();
        using (var f = HostFixture.Start(cfg => {
            cfg.HeartbeatSeconds = 0f;
            cfg.TunnelStateChanged += (s, d) => { lock (states) states.Add(new[] { s, d }); };
            cfg.JoinUrlChanged += u => { lock (urls) urls.Add(u); };
        })) {
            var host = f.Host;
            var fake = new PmcTunnel();
            HostMirror.SetTunnel(host, fake);
            HostMirror.SetSeam(fake, "LocalPort", host.BoundPort);
            HostMirror.SetSeam(fake, "IsProcessAlive", true);
            urls.Clear();

            HostMirror.Emit(fake, "starting", "");
            Assert.That(f.Until(() => {
                lock (states) return states.Count == 1 && states[0][0] == "starting";
            }), Is.True, "starting forwarded");

            HostMirror.Emit(fake, "ready", "https://random-words.trycloudflare.com");
            Assert.That(f.Until(() => {
                lock (states) return states.Count == 2;
            }), Is.True, "ready forwarded");
            lock (states) {
                Assert.That(states[1][0], Is.EqualTo("ready"));
                Assert.That(states[1][1], Is.EqualTo("https://random-words.trycloudflare.com"),
                    "ready carries URL");
            }
            Assert.That(host.AdvertiseUrl, Is.EqualTo("https://random-words.trycloudflare.com"),
                "advertise_url set");
            Assert.That(host.JoinCode.Length, Is.EqualTo(6), "6-letter join code generated while tunneled");
            foreach (char ch in host.JoinCode) {
                Assert.That(ch >= 'A' && ch <= 'Z', Is.True, "code is uppercase letters");
            }
            lock (urls) Assert.That(urls.Count, Is.EqualTo(1), "one join_url_changed on ready");
            Assert.That(host.JoinUrl(),
                Is.EqualTo("https://random-words.trycloudflare.com/?code=" + host.JoinCode),
                "tunnel join URL");

            host.StopTunnel();
            Assert.That(fake.State, Is.EqualTo("stopped"), "stop_tunnel stops the tunnel");
            lock (states) Assert.That(states[states.Count - 1][0], Is.EqualTo("stopped"),
                "stopped state emitted");
            Assert.That(host.AdvertiseUrl, Is.EqualTo(""), "advertise_url restored");
            Assert.That(host.JoinCode, Is.EqualTo(""), "generated code cleared");
            Assert.That(host.GetTunnel(), Is.Null, "tunnel released");
        }
    }

    [Test]
    public void TunnelFailureKeepsUserCode() {
        var states = new List<string[]>();
        using (var f = HostFixture.Start(cfg => {
            cfg.HeartbeatSeconds = 0f;
            cfg.TunnelStateChanged += (s, d) => { lock (states) states.Add(new[] { s, d }); };
        })) {
            var host = f.Host;
            host.JoinCode = "KEEP";
            var fake = new PmcTunnel();
            HostMirror.SetTunnel(host, fake);
            HostMirror.SetSeam(fake, "LocalPort", host.BoundPort);
            HostMirror.SetSeam(fake, "IsProcessAlive", true);
            HostMirror.Emit(fake, "ready", "https://x.trycloudflare.com");
            Assert.That(f.Until(() => {
                lock (states) return states.Count > 0 && states[states.Count - 1][0] == "ready";
            }), Is.True);
            Assert.That(host.JoinCode, Is.EqualTo("KEEP"), "existing join code kept");
            HostMirror.Emit(fake, "failed", "process exited");
            Assert.That(f.Until(() => {
                lock (states) return states[states.Count - 1][0] == "failed";
            }), Is.True, "failed forwarded");
            lock (states) Assert.That(states[states.Count - 1][1], Is.EqualTo("process exited"),
                "failed carries reason");
            Assert.That(host.AdvertiseUrl, Is.EqualTo(""), "advertise restored after failure");
            Assert.That(host.JoinCode, Is.EqualTo("KEEP"), "user code untouched");
        }
    }

    [Test]
    public void CallerSuppliedTunnelCodeUsed() {
        using (var f = HostFixture.Start(cfg => { cfg.HeartbeatSeconds = 0f; })) {
            var host = f.Host;
            host.StartTunnel("WXYZ"); // stub fails, but the pending code is latched
            Assert.That(f.Until(() => host.GetTunnel() == null), Is.True,
                "stub tunnel failed and cleared before the fake is injected");
            var fake = new PmcTunnel();
            HostMirror.SetTunnel(host, fake);
            HostMirror.SetSeam(fake, "LocalPort", host.BoundPort);
            HostMirror.SetSeam(fake, "IsProcessAlive", true);
            HostMirror.Emit(fake, "ready", "https://x.trycloudflare.com");
            Assert.That(f.Until(() => host.JoinCode == "WXYZ"), Is.True, "param code used");
            Assert.That(host.JoinUrl(), Does.Contain("code=WXYZ"));
            host.StopTunnel();
            Assert.That(host.JoinCode, Is.EqualTo("WXYZ"), "caller code kept after stop");
        }
    }

    [Test]
    public void RollingRestartBroadcastsMoved() {
        using (var f = HostFixture.Start(cfg => { cfg.HeartbeatSeconds = 0f; })) {
            var host = f.Host;
            var old = new PmcTunnel();
            HostMirror.SetTunnel(host, old);
            HostMirror.SetSeam(old, "LocalPort", host.BoundPort);
            HostMirror.SetSeam(old, "IsProcessAlive", true);
            HostMirror.Emit(old, "ready", "https://first-words.trycloudflare.com");
            Assert.That(f.Until(() => host.AdvertiseUrl.Contains("first-words")), Is.True);

            using (var ws = new TestWs(f.Port)) {
                ws.Upgrade();
                ws.Hello(new JObject { ["code"] = host.JoinCode });

                var pending = new PmcTunnel();
                HostMirror.SetPendingTunnel(host, pending);
                HostMirror.SetSeam(pending, "LocalPort", host.BoundPort);
                HostMirror.SetSeam(pending, "IsProcessAlive", true);
                HostMirror.Emit(pending, "ready", "https://second-words.trycloudflare.com");
                var moved = ws.RecvJson();
                Assert.That((string)moved["t"], Is.EqualTo("pmc.moved"),
                    "pmc.moved received before the old tunnel died");
                Assert.That((string)moved["d"]["url"], Is.EqualTo(host.JoinUrl()),
                    "moved carries the new join URL");
                Assert.That((string)moved["d"]["url"], Does.Contain("second-words"));
            }
            Assert.That(host.JoinUrl(), Does.Contain("second-words"),
                "join URL now points at the new tunnel");
            Assert.That(old.State, Is.EqualTo("stopped"), "old tunnel stopped after the swap");
            Assert.That(host.GetTunnel(), Is.Not.Null.And.Not.SameAs(old), "new tunnel promoted");
        }
    }

    [Test]
    public void KeptTunnelSurvivesStopStart() {
        int port = FreePort();
        var host = new PmcHostCore { Port = port, ControllerDir = "", HeartbeatSeconds = 0f };
        try {
            Assert.That(host.Start(), Is.EqualTo(0));
            var fake = new PmcTunnel();
            HostMirror.SetTunnel(host, fake);
            HostMirror.SetSeam(fake, "LocalPort", port);
            HostMirror.SetSeam(fake, "IsProcessAlive", true);
            HostMirror.Emit(fake, "ready", "https://kept.trycloudflare.com");
            long deadline = Environment.TickCount64 + 4000;
            while (host.AdvertiseUrl == "" && Environment.TickCount64 < deadline) {
                host.Poll(16);
                System.Threading.Thread.Sleep(5);
            }
            string url = host.JoinUrl();
            host.Stop(); // must NOT kill a live tunnel
            Assert.That(fake.State, Is.EqualTo("ready"), "cloudflared still up after host.Stop()");
            Assert.That(host.Start(), Is.EqualTo(0));
            Assert.That(host.GetTunnel(), Is.SameAs(fake), "same tunnel reused");
            long d2 = Environment.TickCount64 + 4000;
            while (host.JoinUrl() != url && Environment.TickCount64 < d2) {
                host.Poll(16);
                System.Threading.Thread.Sleep(5);
            }
            Assert.That(host.JoinUrl(), Is.EqualTo(url), "join URL unchanged");
        } finally {
            host.Dispose();
        }
    }

    [Test]
    public void DetachedTunnelIsAdopted() {
        int port = FreePort();
        var host = new PmcHostCore { Port = port, ControllerDir = "", HeartbeatSeconds = 0f };
        var fake = new PmcTunnel();
        try {
            Assert.That(host.Start(), Is.EqualTo(0));
            HostMirror.SetTunnel(host, fake);
            HostMirror.SetSeam(fake, "LocalPort", port);
            HostMirror.SetSeam(fake, "IsProcessAlive", true);
            HostMirror.Emit(fake, "ready", "https://detached.trycloudflare.com");
            long deadline = Environment.TickCount64 + 4000;
            while (host.AdvertiseUrl == "" && Environment.TickCount64 < deadline) {
                host.Poll(16);
                System.Threading.Thread.Sleep(5);
            }
            Assert.That(host.DetachTunnel(), Is.True, "tunnel detached");
        } finally {
            host.Dispose();
        }
        Assert.That(fake.State, Is.EqualTo("ready"), "tunnel outlived the host");
        var host2 = new PmcHostCore { Port = port, ControllerDir = "", HeartbeatSeconds = 0f };
        try {
            Assert.That(host2.Start(), Is.EqualTo(0));
            host2.StartTunnel();
            long deadline = Environment.TickCount64 + 4000;
            while (host2.GetTunnel() != fake && Environment.TickCount64 < deadline) {
                host2.Poll(16);
                System.Threading.Thread.Sleep(5);
            }
            Assert.That(host2.GetTunnel(), Is.SameAs(fake), "adopted by the new host");
            Assert.That(host2.JoinUrl(), Does.StartWith("https://detached.trycloudflare.com"),
                "same URL kept");
        } finally {
            host2.Dispose();
        }
    }

    [Test]
    public void LostTunnelAutoRestarts() {
        var states = new List<string>();
        using (var f = HostFixture.Start(cfg => {
            cfg.HeartbeatSeconds = 0f;
            cfg.TunnelRestartDelaySec = 0.1f;
            cfg.TunnelStateChanged += (s, d) => { lock (states) states.Add(s); };
        })) {
            var host = f.Host;
            var fake = new PmcTunnel();
            HostMirror.SetTunnel(host, fake);
            HostMirror.SetSeam(fake, "LocalPort", host.BoundPort);
            HostMirror.SetSeam(fake, "IsProcessAlive", true);
            HostMirror.Emit(fake, "ready", "https://dying.trycloudflare.com");
            Assert.That(f.Until(() => host.AdvertiseUrl.Contains("dying")), Is.True);
            // The process dies: IsProcessAlive false + "lost" -> restore + auto-restart.
            HostMirror.SetSeam(fake, "IsProcessAlive", false);
            HostMirror.Emit(fake, "lost", "process gone");
            Assert.That(f.Until(() => {
                lock (states) return states.Contains("lost");
            }), Is.True, "lost reported");
            // The auto-restart creates a new (stub) tunnel whose Start fails -> "failed" follows.
            Assert.That(f.Until(() => {
                lock (states) return states.Contains("failed");
            }, 6000), Is.True, "auto-restart attempted (stub Start fails)");
            Assert.That(host.AdvertiseUrl, Is.EqualTo(""), "advertise restored after loss");
        }
    }
}
