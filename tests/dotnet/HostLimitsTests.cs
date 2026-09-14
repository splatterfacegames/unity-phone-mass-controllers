// Port of test_host_limits.gd — per-address join-code blocking, CF-Connecting-IP behind the
// tunnel, per-address connection caps, per-address + global admin-PIN budgets, tunneled
// meta-endpoint gating, require_player/players_only mounts.

using System;
using System.IO;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Splatter.Pmc;

[TestFixture]
internal sealed class HostLimitsTests {
    private HostFixture _f;
    private int Port { get { return _f.Port; } }
    private PmcHostCore Host { get { return _f.Host; } }

    [SetUp]
    public void SetUp() {
        _f = HostFixture.Start(cfg => {
            cfg.HeartbeatSeconds = 0f;
            cfg.GraceSeconds = 0f;
            cfg.JoinCode = "GOOD";
            cfg.JoinCodeMaxFailures = 10;
            cfg.JoinCodeBlockSeconds = 6f;
            cfg.MaxConnectionsPerAddress = 0;
        });
    }

    [TearDown]
    public void TearDown() {
        _f?.Dispose();
    }

    /// <summary>Opens a ws, sends pmc.hello with {"code": code}, returns the first
    /// welcome/reject JSON. Empty code is still sent as a field (not counted as a guess).</summary>
    private JObject HelloReject(string code, string headers = "") {
        var ws = new TestWs(Port);
        try {
            ws.Upgrade("/pmc/ws", headers);
        } catch (Exception) {
            ws.Dispose();
            return new JObject { ["t"] = "open failed" };
        }
        ws.SendText(new JObject { ["t"] = "pmc.hello", ["sdk"] = 1, ["code"] = code }
            .ToString(Newtonsoft.Json.Formatting.None));
        var r = ws.RecvJson();
        ws.Dispose();
        return r;
    }

    /// <summary>The Godot suite's host._tunnel = FakeTunnel — inject the stub through the same
    /// field so BehindTunnel/MetaEndpointAllowed see a tunnel.</summary>
    private void SetFakeTunnel(bool on) {
        if (on) {
            var t = new PmcTunnel();
            HostMirror.Emit(t, "ready", "https://fake.trycloudflare.com"); // sets State/Url only; no subscribers yet
            HostMirror.SetTunnel(Host, t);
        } else {
            HostMirror.SetTunnel(Host, null);
        }
    }

    [Test]
    public void WrongJoinCodesBlockTheAddress() {
        for (int i = 0; i < 9; i++) {
            var r = HelloReject("BAD" + i);
            Assert.That((string)r["code"], Is.EqualTo("bad_code"), "wrong code " + (i + 1) + " rejected");
        }
        Assert.That((string)HelloReject("GOOD")["t"], Is.EqualTo("pmc.welcome"),
            "9 failures: correct code still works");
        Assert.That((string)HelloReject("NOPE")["code"], Is.EqualTo("bad_code"), "10th wrong code");
        var blocked = HelloReject("GOOD");
        Assert.That((string)blocked["code"], Is.EqualTo("bad_code"), "blocked: correct code refused");
        Assert.That((string)blocked["reason"], Does.Contain("too many"), "blocked reason explains");
        Assert.That((string)HelloReject("")["code"], Is.EqualTo("bad_code"),
            "missing code while blocked");
        System.Threading.Thread.Sleep(6300);
        Assert.That((string)HelloReject("good")["t"], Is.EqualTo("pmc.welcome"), "block expires");
        for (int i = 0; i < 12; i++) HelloReject("");
        Assert.That((string)HelloReject("GOOD")["t"], Is.EqualTo("pmc.welcome"),
            "empty codes never trigger the block");
    }

    [Test]
    public void KnownTokensBypassTheBlock() {
        Host.GraceSeconds = 5f;
        string token;
        using (var keeper = new TestWs(Port)) {
            keeper.Upgrade();
            keeper.SendText(new JObject { ["t"] = "pmc.hello", ["sdk"] = 1, ["code"] = "GOOD" }
                .ToString(Newtonsoft.Json.Formatting.None));
            var kw = keeper.RecvJson();
            Assert.That((string)kw["t"], Is.EqualTo("pmc.welcome"));
            token = (string)kw["token"];
        }
        for (int i = 0; i < 10; i++) HelloReject("WRONG");
        Assert.That((string)HelloReject("GOOD")["code"], Is.EqualTo("bad_code"),
            "address blocked again");
        using (var back = new TestWs(Port)) {
            back.Upgrade();
            back.SendText(new JObject { ["t"] = "pmc.hello", ["sdk"] = 1, ["token"] = token }
                .ToString(Newtonsoft.Json.Formatting.None));
            var w = back.RecvJson();
            Assert.That((string)w["t"], Is.EqualTo("pmc.welcome"));
            Assert.That((bool)w["rejoined"], Is.True, "rejoin with token works while blocked");
        }
    }

    [Test]
    public void CfConnectingIpBehindTheTunnel() {
        using (var plain = new TestWs(Port)) {
            plain.Upgrade("/pmc/ws", "CF-Connecting-IP: 203.0.113.7\r\n");
            var pw = plain.Hello(new JObject { ["code"] = "GOOD" });
            int id = (int)pw["id"];
            Assert.That(_f.Until(() => Host.GetPlayer(id) != null), Is.True);
            Assert.That(Host.GetPlayer(id).RemoteAddress, Is.EqualTo("127.0.0.1"),
                "header ignored without a tunnel");
        }

        SetFakeTunnel(true);
        using (var via = new TestWs(Port)) {
            via.Upgrade("/pmc/ws", "CF-Connecting-IP: 203.0.113.7\r\n");
            var vw = via.Hello(new JObject { ["code"] = "GOOD" });
            int vid = (int)vw["id"];
            Assert.That(_f.Until(() => Host.GetPlayer(vid) != null), Is.True);
            Assert.That(Host.GetPlayer(vid).RemoteAddress, Is.EqualTo("203.0.113.7"),
                "remote_address from CF-Connecting-IP behind tunnel");
        }
        using (var junk = new TestWs(Port)) {
            junk.Upgrade("/pmc/ws", "CF-Connecting-IP: not-an-ip\r\n");
            var jw = junk.Hello(new JObject { ["code"] = "GOOD" });
            int jid = (int)jw["id"];
            Assert.That(_f.Until(() => Host.GetPlayer(jid) != null), Is.True);
            Assert.That(Host.GetPlayer(jid).RemoteAddress, Is.EqualTo("127.0.0.1"),
                "invalid CF-Connecting-IP falls back to the peer");
        }
        for (int i = 0; i < 10; i++) {
            HelloReject("WRONG", "CF-Connecting-IP: 203.0.113.7\r\n");
        }
        Assert.That((string)HelloReject("GOOD", "CF-Connecting-IP: 203.0.113.7\r\n")["code"],
            Is.EqualTo("bad_code"), "tunneled address blocked");
        Assert.That((string)HelloReject("GOOD", "CF-Connecting-IP: 198.51.100.9\r\n")["t"],
            Is.EqualTo("pmc.welcome"), "other tunneled address unaffected");
        Assert.That((string)HelloReject("GOOD")["t"], Is.EqualTo("pmc.welcome"),
            "loopback (no header) unaffected");
        SetFakeTunnel(false);
        Assert.That((string)HelloReject("GOOD", "CF-Connecting-IP: 203.0.113.7\r\n")["t"],
            Is.EqualTo("pmc.welcome"), "header not trusted once the tunnel is gone");
    }

    [Test]
    public void PerAddressConnectionCap() {
        Host.MaxConnectionsPerAddress = 3;
        var socks = new TestHttp[3];
        for (int i = 0; i < 3; i++) socks[i] = new TestHttp(Port);
        System.Threading.Thread.Sleep(150);
        using (var fourth = new TestHttp(Port)) {
            Assert.That(fourth.WaitClosed(3000), Is.True,
                "4th connection from the same address refused");
        }
        var r = socks[0].Exchange("GET /pmc/healthz HTTP/1.1\r\nHost: x\r\n\r\n");
        Assert.That(r.Status, Is.EqualTo(200), "existing connections keep working");
        socks[1].Dispose();
        System.Threading.Thread.Sleep(700); // reap on the next timer tick
        using (var again = new TestHttp(Port)) {
            var r2 = again.Exchange("GET /pmc/healthz HTTP/1.1\r\nHost: x\r\n\r\n");
            Assert.That(r2.Status, Is.EqualTo(200), "slot freed after a close");
        }
        foreach (var s in socks) s.Dispose();
        System.Threading.Thread.Sleep(600);
    }

    [Test]
    public void PerAddressCapBehindTheTunnel() {
        Host.MaxConnectionsPerAddress = 3;
        SetFakeTunnel(true);
        var socks = new TestHttp[3];
        try {
            for (int i = 0; i < 3; i++) {
                socks[i] = new TestHttp(Port);
                var r = socks[i].Exchange("GET /pmc/healthz HTTP/1.1\r\nHost: x\r\n"
                    + "CF-Connecting-IP: 192.0.2.1\r\n\r\n");
                Assert.That(r.Status, Is.EqualTo(200),
                    "tunneled connection " + (i + 1) + " from 192.0.2.1");
            }
            using (var over = new TestHttp(Port)) {
                var ro = over.Exchange("GET /pmc/healthz HTTP/1.1\r\nHost: x\r\n"
                    + "CF-Connecting-IP: 192.0.2.1\r\n\r\n");
                Assert.That(ro.Status, Is.EqualTo(429),
                    "4th tunneled connection from the same client -> 429");
            }
            using (var other = new TestHttp(Port)) {
                var ro = other.Exchange("GET /pmc/healthz HTTP/1.1\r\nHost: x\r\n"
                    + "CF-Connecting-IP: 192.0.2.2\r\n\r\n");
                Assert.That(ro.Status, Is.EqualTo(200),
                    "different client address through the same tunnel is fine");
            }
        } finally {
            foreach (var s in socks) s?.Dispose();
            SetFakeTunnel(false);
        }
    }

    [Test]
    public void AdminPinFailuresPerAddress() {
        Host.AdminPin = "1357";
        for (int conn = 0; conn < 5; conn++) {
            using (var ws = new TestWs(Port)) {
                ws.Upgrade();
                ws.Hello(new JObject { ["code"] = "GOOD" });
                for (int i = 0; i < 5; i++) {
                    ws.SendText(new JObject { ["t"] = "pmc.auth", ["pin"] = "0000" }
                        .ToString(Newtonsoft.Json.Formatting.None));
                    Assert.That((bool)ws.RecvJson()["ok"], Is.False,
                        "wrong pin (connection " + (conn + 1) + ", try " + (i + 1) + ")");
                }
            }
        }
        using (var fresh = new TestWs(Port)) {
            fresh.Upgrade();
            fresh.Hello(new JObject { ["code"] = "GOOD" });
            fresh.SendText(new JObject { ["t"] = "pmc.auth", ["pin"] = "1357" }
                .ToString(Newtonsoft.Json.Formatting.None));
            var res = fresh.RecvJson();
            Assert.That((bool)res["ok"], Is.False,
                "after 20+ failures from one address, a new connection is still locked out");
            Assert.That((long)res["locked_ms"], Is.GreaterThan(30000), "address lockout ~60 s");
        }
    }

    [Test]
    public void GlobalPinBudgetDisablesThePin() {
        Host.AdminPin = "1357";
        for (int i = 0; i < 21; i++) {
            using (var ws = new TestWs(Port)) {
                ws.Upgrade();
                ws.Hello(new JObject { ["code"] = "GOOD" });
                ws.SendText(new JObject { ["t"] = "pmc.auth", ["pin"] = "0000" }
                    .ToString(Newtonsoft.Json.Formatting.None));
                ws.RecvJson();
            }
        }
        // A fresh address (through the tunnel) must still be refused — the PIN is dead globally.
        SetFakeTunnel(true);
        using (var dws = new TestWs(Port)) {
            dws.Upgrade("/pmc/ws", "CF-Connecting-IP: 203.0.113.99\r\n");
            dws.Hello(new JObject { ["code"] = "GOOD" });
            dws.SendText(new JObject { ["t"] = "pmc.auth", ["pin"] = "1357" }
                .ToString(Newtonsoft.Json.Formatting.None));
            var res = dws.RecvJson();
            Assert.That((bool)res["ok"], Is.False,
                "correct PIN refused after the global budget is spent");
            Assert.That((bool)res["disabled"], Is.True, "disabled flag reported");
        }
        SetFakeTunnel(false);
    }

    [Test]
    public void MetaEndpointsGatedWhileTunneled() {
        SetFakeTunnel(true);
        var noCode = Fetch("/pmc/info.json", "CF-Connecting-IP: 203.0.113.7\r\n");
        Assert.That(noCode.Status, Is.EqualTo(403), "info.json refuses a remote request without a code");
        var withCode = Fetch("/pmc/info.json?code=good", "CF-Connecting-IP: 203.0.113.7\r\n");
        Assert.That(withCode.Status, Is.EqualTo(200), "info.json answers with the code (case-insensitive)");
        var badCode = Fetch("/pmc/info.json?code=XXXX", "CF-Connecting-IP: 203.0.113.7\r\n");
        Assert.That(badCode.Status, Is.EqualTo(403), "info.json refuses a wrong code");
        var qr = Fetch("/pmc/qr.png", "CF-Connecting-IP: 203.0.113.7\r\n");
        Assert.That(qr.Status, Is.EqualTo(403), "qr.png refuses a remote request");
        var local = Fetch("/pmc/info.json", "");
        Assert.That(local.Status, Is.EqualTo(200), "info.json stays open to loopback");
        var js = Fetch("/pmc/pmc.js", "CF-Connecting-IP: 203.0.113.7\r\n");
        Assert.That(js.Status, Is.Not.EqualTo(403), "pmc.js stays public while tunneled");
        SetFakeTunnel(false);
    }

    [Test]
    public void RequirePlayerAndPlayersOnlyMounts() {
        string priv = Path.Combine(Path.GetTempPath(), "pmc-priv-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(priv);
        File.WriteAllText(Path.Combine(priv, "save.dat"), "save bytes");
        try {
            string token;
            int pid;
            using (var pws = new TestWs(Port)) {
                pws.Upgrade();
                var w = pws.Hello(new JObject { ["code"] = "GOOD" });
                token = (string)w["token"];
                pid = (int)w["id"];

                Host.AddRoute("/who", req => {
                    var pl = Host.RequirePlayer(req);
                    if (pl == null) return PmcHttpResponse.Error(403);
                    return PmcHttpResponse.Json(new JObject { ["id"] = pl.Id });
                });
                Assert.That(Fetch("/who", "").Status, Is.EqualTo(403), "route 403 without a token");
                var r = Fetch("/who?t=" + token, "");
                Assert.That((int)JObject.Parse(r.BodyText())["id"], Is.EqualTo(pid),
                    "?t= maps to the player");
                Assert.That(Fetch("/who", "Cookie: pmc_token=" + token + "; other=x\r\n").Status,
                    Is.EqualTo(200), "pmc_token cookie maps to the player");
                Assert.That(Fetch("/who?t=deadbeef", "").Status, Is.EqualTo(403), "unknown token 403");

                Host.ServeDirectory("/priv", priv, true);
                Assert.That(Fetch("/priv/save.dat", "").Status, Is.EqualTo(403),
                    "players_only dir refuses anonymous");
                Assert.That(Fetch("/priv/save.dat?t=" + token, "").BodyText(),
                    Is.EqualTo("save bytes"), "players_only dir serves with ?t=");
                Assert.That(Fetch("/priv/save.dat", "Cookie: pmc_token=" + token + "\r\n").Status,
                    Is.EqualTo(200), "players_only dir serves with cookie");
            }
            Assert.That(_f.Until(() => Host.GetPlayer(pid) == null), Is.True,
                "grace_seconds = 0: player gone after close");
            Assert.That(Fetch("/who?t=" + token, "").Status, Is.EqualTo(403),
                "gone player's token stops working");
        } finally {
            try { Directory.Delete(priv, true); } catch (Exception) { }
        }
    }

    private HttpResp Fetch(string path, string headers) {
        using (var s = new TestHttp(Port)) {
            return s.Exchange("GET " + path + " HTTP/1.1\r\nHost: x\r\nConnection: close\r\n"
                + headers + "\r\n");
        }
    }
}
