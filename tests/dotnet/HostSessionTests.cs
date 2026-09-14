// Port of test_host.gd — sessions: hello/welcome, rejoin/grace/tombstones, join code, full,
// admin auth + lockout, kick/ban/remember, replaced, leave, heartbeat. One host per test,
// config mirrors _make_host: grace 0.5, remember 60, heartbeat 0, admin_pin 2468.

using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Splatter.Pmc;

[TestFixture]
internal sealed class HostSessionTests {
    private HostFixture _f;
    private readonly List<object[]> _log = new List<object[]>();

    private int Port { get { return _f.Port; } }
    private PmcHostCore Host { get { return _f.Host; } }

    [SetUp]
    public void SetUp() {
        lock (_log) _log.Clear();
        _f = HostFixture.Start(cfg => {
            cfg.GraceSeconds = 0.5f;
            cfg.RememberSeconds = 60f;
            cfg.HeartbeatSeconds = 0f;
            cfg.AdminPin = "2468";
            cfg.MaxConnectionsPerAddress = 0;
            cfg.PlayerJoined += p => Log("joined", p);
            cfg.PlayerRejoined += p => Log("rejoined", p);
            cfg.PlayerDisconnected += p => Log("disconnected", p);
            cfg.PlayerLeft += (p, r) => Log("left", p, r);
            cfg.PlayerUpdated += p => Log("updated", p);
            cfg.AdminAuthenticated += p => Log("admin", p);
            cfg.MessageReceived += (p, d) => Log("message", p, d);
        });
    }

    [TearDown]
    public void TearDown() {
        _f?.Dispose();
    }

    private void Log(string kind, PmcPlayer p, object extra = null) {
        lock (_log) _log.Add(new object[] { kind, p.Id, extra });
    }

    private object[] Seen(string kind, int id = -1) {
        lock (_log) {
            foreach (var e in _log) {
                if ((string)e[0] == kind && (id < 0 || (int)e[1] == id)) return e;
            }
        }
        return null;
    }

    private bool WaitSeen(string kind, int id = -1, int timeoutMs = 4000) {
        return _f.Until(() => Seen(kind, id) != null, timeoutMs);
    }

    private TestWs Join(JObject extra = null) {
        var ws = new TestWs(Port);
        ws.Upgrade();
        ws.Hello(extra);
        return ws;
    }

    /// <summary>Sends pmc.hello and returns whatever the first text frame is (welcome or reject).</summary>
    private static JObject HelloRaw(TestWs ws, JObject extra = null) {
        var hello = new JObject { ["t"] = "pmc.hello", ["sdk"] = 1 };
        if (extra != null) {
            foreach (var kv in extra) hello[kv.Key] = kv.Value;
        }
        ws.SendText(hello.ToString(Newtonsoft.Json.Formatting.None));
        return ws.RecvJson();
    }

    private static void SendJson(TestWs ws, JObject m) {
        ws.SendText(m.ToString(Newtonsoft.Json.Formatting.None));
    }

    [Test]
    public void HelloWelcome() {
        var ws = Join(new JObject { ["name"] = "  Ann\u0007ie  ", ["profile"] = new JObject { ["color"] = "red" } });
        var w = ws.LastWelcome;
        Assert.That((string)w["name"], Is.EqualTo("Annie"), "name trimmed and control chars stripped");
        Assert.That((string)w["profile"]["color"], Is.EqualTo("red"), "profile echoed");
        Assert.That((bool)w["rejoined"], Is.False);
        Assert.That((bool)w["admin"], Is.False);
        Assert.That(((string)w["token"]).Length, Is.EqualTo(32), "128-bit hex token");
        Assert.That(w["server_ms"], Is.Not.Null);
        Assert.That(w["join_url"], Is.Not.Null);
        Assert.That(w["id"], Is.Not.Null);
        int id = (int)w["id"];
        Assert.That(_f.Until(() => Host.GetPlayer(id) != null, 4000), Is.True);
        var p = Host.GetPlayer(id);
        Assert.That(p.Connected, Is.True);
        Assert.That(p.Name, Is.EqualTo("Annie"));
        Assert.That(p.RemoteAddress, Is.EqualTo("127.0.0.1"));
        Assert.That(WaitSeen("joined", id), Is.True, "player_joined emitted");
        ws.Dispose();

        var longName = Join(new JObject { ["name"] = new string('x', 100) });
        Assert.That(((string)longName.LastWelcome["name"]).Length, Is.EqualTo(32), "long name truncated");
        var anon = Join();
        Assert.That((string)anon.LastWelcome["name"],
            Is.EqualTo("Player " + (int)anon.LastWelcome["id"]), "default name");
        Assert.That((int)longName.LastWelcome["id"], Is.GreaterThan(id), "ids increase");
        Assert.That((int)anon.LastWelcome["id"], Is.GreaterThan((int)longName.LastWelcome["id"]));

        var unknown = new TestWs(Port);
        unknown.Upgrade();
        var uw = HelloRaw(unknown, new JObject { ["token"] = "deadbeefdeadbeefdeadbeefdeadbeef" });
        Assert.That((string)uw["t"], Is.EqualTo("pmc.welcome"));
        Assert.That((string)uw["token"], Is.Not.EqualTo("deadbeefdeadbeefdeadbeefdeadbeef"),
            "unknown token gets a fresh token");
        Assert.That((bool)uw["rejoined"], Is.False);

        var ids = new List<int>();
        foreach (var pl in Host.Players()) ids.Add(pl.Id);
        var sorted = new List<int>(ids);
        sorted.Sort();
        Assert.That(ids, Is.EqualTo(sorted), "players() ordered by id");
        foreach (var c in new[] { longName, anon, unknown }) c.Dispose();
    }

    [Test]
    public void HelloRejects() {
        using (var ver = new TestWs(Port)) {
            ver.Upgrade();
            SendJson(ver, new JObject { ["t"] = "pmc.hello", ["sdk"] = 2 });
            var rej = ver.RecvJson();
            Assert.That((string)rej["code"], Is.EqualTo("version"), "sdk 2 -> reject version");
            Assert.That(ver.RecvClose(), Is.EqualTo(4000), "reject closes 4000");
        }
        using (var nosdk = new TestWs(Port)) {
            nosdk.Upgrade();
            SendJson(nosdk, new JObject { ["t"] = "pmc.hello" });
            Assert.That((string)nosdk.RecvJson()["code"], Is.EqualTo("bad_hello"), "missing sdk -> bad_hello");
        }
        using (var other = new TestWs(Port)) {
            other.Upgrade();
            SendJson(other, new JObject { ["t"] = "msg", ["d"] = 1 });
            Assert.That((string)other.RecvJson()["code"], Is.EqualTo("bad_hello"), "msg before hello -> bad_hello");
        }
    }

    [Test]
    public void Messaging() {
        var wa = Join(new JObject { ["name"] = "A" });
        var wb = Join(new JObject { ["name"] = "B" });
        int ida = (int)wa.LastWelcome["id"];
        int idb = (int)wb.LastWelcome["id"];

        SendJson(wa, new JObject { ["t"] = "msg", ["d"] = new JObject { ["press"] = true, ["n"] = 3 } });
        Assert.That(_f.Until(() => {
            var m = Seen("message", ida);
            return m != null && m[2] is JObject
                && (bool)((JObject)m[2])["press"] && (int)((JObject)m[2])["n"] == 3;
        }), Is.True, "message_received with d");

        wa.Send(2, new byte[] { 9, 8, 7 });
        Assert.That(_f.Until(() => {
            lock (_log) {
                foreach (var e in _log) {
                    if ((string)e[0] != "message" || (int)e[1] != ida) continue;
                    var arr = e[2] as byte[];
                    if (arr != null && arr.Length == 3 && arr[0] == 9 && arr[1] == 8 && arr[2] == 7) {
                        return true;
                    }
                }
            }
            return false;
        }), Is.True, "binary message_received as byte[]");

        Host.Send(ida, new JObject { ["hi"] = "a" });
        Assert.That((string)((JObject)wa.RecvJson()["d"])["hi"], Is.EqualTo("a"), "send by id");
        Host.Send(Host.GetPlayer(idb), new JArray { 1, "two" });
        Assert.That(wb.RecvJson()["d"].ToString(Newtonsoft.Json.Formatting.None),
            Is.EqualTo("[1,\"two\"]"), "send by PmcPlayer");
        Host.Send(idb, "just a string");
        Assert.That((string)wb.RecvJson()["d"], Is.EqualTo("just a string"), "send string");
        Host.Send(idb, 42);
        Assert.That((int)wb.RecvJson()["d"], Is.EqualTo(42), "send number");
        Host.Send(idb, new byte[] { 1, 2, 3 });
        var be = wb.Recv();
        Assert.That(be.Op, Is.EqualTo(2));
        Assert.That(be.Payload, Is.EqualTo(new byte[] { 1, 2, 3 }), "send binary");
        Host.Send(9999, new JObject { ["nobody"] = true }); // no-op, must not throw

        Host.Broadcast(new JObject { ["all"] = 1 });
        Assert.That((int)((JObject)wa.RecvJson()["d"])["all"], Is.EqualTo(1), "broadcast reaches A");
        Assert.That((int)((JObject)wb.RecvJson()["d"])["all"], Is.EqualTo(1), "broadcast reaches B");
        Host.Broadcast(new JObject { ["only"] = "B" }, p => p.Id == idb);
        Assert.That((string)((JObject)wb.RecvJson()["d"])["only"], Is.EqualTo("B"), "filtered broadcast B");
        try {
            var skip = wa.Recv(400);
            Assert.Fail("filtered broadcast skipped A but got op " + skip.Op);
        } catch (TimeoutException) {
        } catch (System.Net.Sockets.SocketException) {
        }

        SendJson(wa, new JObject { ["t"] = "pmc.ping", ["c"] = 12345.5 });
        var pong = wa.RecvJson();
        Assert.That((string)pong["t"], Is.EqualTo("pmc.pong"));
        Assert.That((double)pong["c"], Is.EqualTo(12345.5), "pong echoes c");
        long epochMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Assert.That(Math.Abs((long)pong["s"] - epochMs), Is.LessThan(5000), "pong s is epoch ms");
        Assert.That(Math.Abs((long)wa.LastWelcome["server_ms"] - epochMs), Is.LessThan(60000),
            "welcome server_ms is epoch ms");

        SendJson(wb, new JObject {
            ["t"] = "pmc.profile", ["name"] = "Bee", ["profile"] = new JObject { ["hat"] = 1 },
        });
        Assert.That(WaitSeen("updated", idb), Is.True, "player_updated after pmc.profile");
        var pb = Host.GetPlayer(idb);
        Assert.That(pb.Name, Is.EqualTo("Bee"));
        Assert.That((int)pb.Profile["hat"], Is.EqualTo(1));
        lock (_log) _log.Clear();
        SendJson(wb, new JObject {
            ["t"] = "pmc.profile", ["name"] = "Bee", ["profile"] = new JObject { ["hat"] = 1 },
        });
        SendJson(wb, new JObject { ["t"] = "pmc.unknown" });
        SendJson(wb, new JObject { ["no_t"] = 1 });
        wb.SendText("[1,2]");
        SendJson(wb, new JObject { ["t"] = "pmc.ping", ["c"] = 1 });
        var pong2 = wb.RecvJson();
        Assert.That((string)pong2["t"], Is.EqualTo("pmc.pong"),
            "unknown/invalid frames after hello are ignored");
        System.Threading.Thread.Sleep(150);
        Assert.That(Seen("updated", idb), Is.Null, "unchanged profile emits nothing");
        wa.Dispose();
        wb.Dispose();
    }

    [Test]
    public void RejoinWithinGrace() {
        Host.GraceSeconds = 2f;
        var ws = Join(new JObject { ["name"] = "Re" });
        int id = (int)ws.LastWelcome["id"];
        string token = (string)ws.LastWelcome["token"];
        Host.GetPlayer(id).Meta["score"] = 7;
        ws.Dispose();
        Assert.That(WaitSeen("disconnected", id), Is.True, "player_disconnected on socket loss");
        var p = Host.GetPlayer(id);
        Assert.That(p, Is.Not.Null);
        Assert.That(p.Connected, Is.False);
        Assert.That(p.GraceDeadlineMs, Is.GreaterThan(Environment.TickCount64), "player kept during grace");
        Assert.That(Host.Players(false).FindIndex(x => x.Id == id), Is.EqualTo(-1),
            "players(false) excludes disconnected");
        Assert.That(Host.Players(true).FindIndex(x => x.Id == id), Is.GreaterThanOrEqualTo(0),
            "players(true) includes disconnected");

        var back = Join(new JObject { ["token"] = token, ["name"] = "Re2" });
        Assert.That((int)back.LastWelcome["id"], Is.EqualTo(id), "same id on rejoin");
        Assert.That((bool)back.LastWelcome["rejoined"], Is.True, "welcome.rejoined");
        Assert.That((string)back.LastWelcome["token"], Is.EqualTo(token), "same token");
        Assert.That(WaitSeen("rejoined", id), Is.True, "player_rejoined emitted");
        Assert.That(Host.GetPlayer(id), Is.SameAs(p), "same PmcPlayer");
        Assert.That((int)p.Meta["score"], Is.EqualTo(7), "meta kept");
        Assert.That(p.Connected, Is.True);
        Assert.That(WaitSeen("updated", id), Is.True, "name change on rejoin -> player_updated");
        Assert.That(p.Name, Is.EqualTo("Re2"));
        Assert.That(Seen("left", id), Is.Null, "no player_left during grace");
        back.Dispose();
    }

    [Test]
    public void GraceExpiryTombstone() {
        Host.GraceSeconds = 0.3f;
        var ws = Join(new JObject { ["name"] = "Re", ["profile"] = new JObject { ["c"] = "g" } });
        int id = (int)ws.LastWelcome["id"];
        string token = (string)ws.LastWelcome["token"];
        Host.GetPlayer(id).Meta["score"] = 7;
        ws.Dispose();
        Assert.That(WaitSeen("left", id), Is.True, "player_left after grace");
        Assert.That((string)Seen("left", id)[2], Is.EqualTo("timeout"), "reason timeout");
        Assert.That(_f.Until(() => Host.GetPlayer(id) == null), Is.True, "player removed");
        lock (_log) _log.Clear();

        var back = Join(new JObject { ["token"] = token });
        Assert.That((int)back.LastWelcome["id"], Is.EqualTo(id), "tombstone restores id");
        Assert.That((bool)back.LastWelcome["rejoined"], Is.True, "tombstone welcome.rejoined");
        Assert.That((string)back.LastWelcome["name"], Is.EqualTo("Re"), "tombstone restores name");
        Assert.That((string)back.LastWelcome["profile"]["c"], Is.EqualTo("g"), "tombstone restores profile");
        var np = Host.GetPlayer(id);
        Assert.That(np, Is.Not.Null);
        Assert.That((int)np.Meta["score"], Is.EqualTo(7), "tombstone restores meta");
        Assert.That(WaitSeen("joined", id), Is.True, "tombstone recall emits player_joined");
        back.Dispose();
    }

    [Test]
    public void RememberZeroDropsTombstone() {
        Host.GraceSeconds = 0.3f;
        Host.RememberSeconds = 0f;
        var ws = Join();
        int id = (int)ws.LastWelcome["id"];
        string token = (string)ws.LastWelcome["token"];
        ws.Dispose();
        Assert.That(WaitSeen("left", id), Is.True);
        var fresh = Join(new JObject { ["token"] = token });
        Assert.That((int)fresh.LastWelcome["id"], Is.Not.EqualTo(id), "no tombstone -> new id");
        Assert.That((string)fresh.LastWelcome["token"], Is.Not.EqualTo(token), "new token");
        fresh.Dispose();
    }

    [Test]
    public void GraceZeroTimesOutImmediately() {
        Host.GraceSeconds = 0f;
        var ws = Join();
        int id = (int)ws.LastWelcome["id"];
        ws.Dispose();
        Assert.That(WaitSeen("disconnected", id), Is.True, "disconnected still emitted");
        Assert.That(WaitSeen("left", id), Is.True, "immediate timeout with grace 0");
        Assert.That((string)Seen("left", id)[2], Is.EqualTo("timeout"));
    }

    [Test]
    public void JoinCode() {
        Host.JoinCode = "ABCD";
        using (var bad = new TestWs(Port)) {
            bad.Upgrade();
            var rej = HelloRaw(bad, new JObject { ["code"] = "ZZZZ" });
            Assert.That((string)rej["code"], Is.EqualTo("bad_code"), "wrong code -> bad_code");
            Assert.That(bad.RecvClose(), Is.EqualTo(4000), "bad_code closes 4000");
        }
        using (var missing = new TestWs(Port)) {
            missing.Upgrade();
            Assert.That((string)HelloRaw(missing)["code"], Is.EqualTo("bad_code"), "missing code -> bad_code");
        }
        var good = Join(new JObject { ["code"] = " abcd " });
        Assert.That((string)good.LastWelcome["t"], Is.EqualTo("pmc.welcome"),
            "code is case-insensitive and trimmed");
        string tok = (string)good.LastWelcome["token"];
        Host.GraceSeconds = 5f;
        good.Dispose();
        Assert.That(WaitSeen("disconnected", (int)good.LastWelcome["id"]), Is.True);
        var rejoin = Join(new JObject { ["token"] = tok });
        Assert.That((bool)rejoin.LastWelcome["rejoined"], Is.True,
            "known token rejoins without code");
        rejoin.Dispose();
    }

    [Test]
    public void MaxPlayers() {
        Host.MaxPlayers = 1;
        var one = Join();
        using (var full = new TestWs(Port)) {
            full.Upgrade();
            Assert.That((string)HelloRaw(full)["code"], Is.EqualTo("full"), "over capacity -> full");
        }
        Host.GraceSeconds = 5f;
        one.Dispose();
        Assert.That(WaitSeen("disconnected", (int)one.LastWelcome["id"]), Is.True);
        var again = Join(new JObject { ["token"] = (string)one.LastWelcome["token"] });
        Assert.That((bool)again.LastWelcome["rejoined"], Is.True, "grace player can rejoin while full");
        again.Dispose();
    }

    [Test]
    public void AdminAuthAndLockout() {
        var ws = Join();
        int id = (int)ws.LastWelcome["id"];
        for (int i = 0; i < 4; i++) {
            SendJson(ws, new JObject { ["t"] = "pmc.auth", ["pin"] = "0000" });
            Assert.That((bool)ws.RecvJson()["ok"], Is.False, "wrong pin " + (i + 1));
        }
        SendJson(ws, new JObject { ["t"] = "pmc.auth", ["pin"] = 2468 });
        Assert.That((bool)ws.RecvJson()["ok"], Is.False, "non-string pin fails (5th failure)");
        SendJson(ws, new JObject { ["t"] = "pmc.auth", ["pin"] = "2468" });
        var locked = ws.RecvJson();
        Assert.That((bool)locked["ok"], Is.False, "correct pin refused during lockout");
        Assert.That((long)locked["locked_ms"], Is.GreaterThan(25000), "lockout ~30 s reported");
        Assert.That(Host.GetPlayer(id).IsAdmin, Is.False, "not admin while locked");
        ws.Dispose();

        var wb = Join();
        int bid = (int)wb.LastWelcome["id"];
        SendJson(wb, new JObject { ["t"] = "pmc.auth", ["pin"] = "2468" });
        Assert.That((bool)wb.RecvJson()["ok"], Is.True,
            "other connection can auth (lockout is per connection)");
        Assert.That(_f.Until(() => Host.GetPlayer(bid) != null && Host.GetPlayer(bid).IsAdmin),
            Is.True, "is_admin set");
        Assert.That(WaitSeen("admin", bid), Is.True, "admin_authenticated emitted");

        Host.GraceSeconds = 5f;
        string tok = (string)wb.LastWelcome["token"];
        wb.Dispose();
        Assert.That(WaitSeen("disconnected", bid), Is.True);
        var back = Join(new JObject { ["token"] = tok });
        Assert.That((bool)back.LastWelcome["admin"], Is.True, "admin kept on rejoin");

        Host.AdminPin = "";
        SendJson(back, new JObject { ["t"] = "pmc.auth", ["pin"] = "" });
        Assert.That((bool)back.RecvJson()["ok"], Is.False, "empty admin_pin never authenticates");
        back.Dispose();
    }

    [Test]
    public void KickBanRemember() {
        var ws = Join();
        int id = (int)ws.LastWelcome["id"];
        string token = (string)ws.LastWelcome["token"];
        Host.Kick(id, "be nice");
        var kicked = ws.RecvJson();
        Assert.That((string)kicked["t"], Is.EqualTo("pmc.kicked"));
        Assert.That((string)kicked["reason"], Is.EqualTo("be nice"), "pmc.kicked with reason");
        Assert.That(ws.RecvClose(), Is.EqualTo(4001), "kick closes 4001");
        Assert.That(WaitSeen("left", id), Is.True);
        Assert.That((string)Seen("left", id)[2], Is.EqualTo("kicked"), "player_left kicked");
        Assert.That(_f.Until(() => Host.GetPlayer(id) == null), Is.True, "kicked player removed");
        Assert.That(Seen("disconnected", id), Is.Null, "no player_disconnected for kick");
        ws.Dispose();

        var again = Join(new JObject { ["token"] = token });
        int againId = (int)again.LastWelcome["id"];
        Assert.That(againId, Is.Not.EqualTo(id), "kicked token without ban joins as new player");
        string againTok = (string)again.LastWelcome["token"];
        Host.Kick(Host.GetPlayer(againId), "bye", ban: true);
        Assert.That(again.RecvClose(), Is.EqualTo(4001));
        again.Dispose();
        using (var banned = new TestWs(Port)) {
            banned.Upgrade();
            Assert.That((string)HelloRaw(banned, new JObject { ["token"] = againTok })["code"],
                Is.EqualTo("banned"), "ban -> reject banned");
        }
        Host.ClearBans();
        var unbanned = Join(new JObject { ["token"] = againTok });
        Assert.That((string)unbanned.LastWelcome["t"], Is.EqualTo("pmc.welcome"), "clear_bans");
        unbanned.Dispose();

        // Kick a player whose socket is already in grace.
        Host.GraceSeconds = 5f;
        var g = Join();
        int gid = (int)g.LastWelcome["id"];
        g.Dispose();
        Assert.That(WaitSeen("disconnected", gid), Is.True);
        lock (_log) _log.Clear();
        Host.Kick(gid);
        Assert.That(WaitSeen("left", gid), Is.True, "kick a player in grace");
        Assert.That((string)Seen("left", gid)[2], Is.EqualTo("kicked"));
    }

    [Test]
    public void KickRememberRestoresPlayer() {
        var ws = Join(new JObject { ["name"] = "Keep" });
        int id = (int)ws.LastWelcome["id"];
        string token = (string)ws.LastWelcome["token"];
        Host.GetPlayer(id).Meta["n"] = 5;
        Host.Kick(id, "afk", remember: true);
        Assert.That(WaitSeen("left", id), Is.True);
        Assert.That((string)Seen("left", id)[2], Is.EqualTo("kicked"),
            "remembered kick still reports kicked");
        ws.Dispose();
        var back = Join(new JObject { ["token"] = token });
        Assert.That((int)back.LastWelcome["id"], Is.EqualTo(id), "remembered kick restores id");
        Assert.That((bool)back.LastWelcome["rejoined"], Is.True, "remembered kick rejoins");
        Assert.That((int)Host.GetPlayer(id).Meta["n"], Is.EqualTo(5), "remembered kick restores meta");
        back.Dispose();
    }

    [Test]
    public void Replacement() {
        var first = Join();
        int id = (int)first.LastWelcome["id"];
        string token = (string)first.LastWelcome["token"];
        var second = Join(new JObject { ["token"] = token });
        Assert.That((int)second.LastWelcome["id"], Is.EqualTo(id),
            "second socket takes over the same player");
        var rep = first.RecvJson();
        Assert.That((string)rep["t"], Is.EqualTo("pmc.replaced"), "old socket gets pmc.replaced");
        Assert.That(first.RecvClose(), Is.EqualTo(4002), "old socket closed 4002");
        first.Dispose();
        System.Threading.Thread.Sleep(200);
        Assert.That(_f.Until(() => Host.GetPlayer(id) != null && Host.GetPlayer(id).Connected),
            Is.True, "player still connected on new socket");
        Assert.That(Seen("disconnected", id), Is.Null, "no player_disconnected when replaced");
        Assert.That(WaitSeen("rejoined", id), Is.True, "player_rejoined when replaced");
        Host.Send(id, "to-new");
        Assert.That((string)second.RecvJson()["d"], Is.EqualTo("to-new"),
            "messages go to the new socket");
        second.Dispose();
    }

    [Test]
    public void Leave() {
        var ws = Join();
        int id = (int)ws.LastWelcome["id"];
        string token = (string)ws.LastWelcome["token"];
        SendJson(ws, new JObject { ["t"] = "pmc.leave" });
        Assert.That(WaitSeen("left", id), Is.True);
        Assert.That((string)Seen("left", id)[2], Is.EqualTo("leave"), "player_left leave (no grace)");
        Assert.That(ws.RecvClose(), Is.EqualTo(1000), "leave closes 1000");
        Assert.That(Seen("disconnected", id), Is.Null, "no player_disconnected on leave");
        ws.Dispose();
        var after = Join(new JObject { ["token"] = token });
        Assert.That((int)after.LastWelcome["id"], Is.Not.EqualTo(id), "no tombstone after leave");
        after.Dispose();
    }

    [Test]
    public void Heartbeat() {
        Host.HeartbeatSeconds = 0.2f;
        var dead = new TestWs(Port);
        dead.Upgrade();
        var dw = HelloRaw(dead);
        Assert.That((string)dw["t"], Is.EqualTo("pmc.welcome"));
        int did = (int)dw["id"];

        var alive = Join();
        int aid = (int)alive.LastWelcome["id"];
        long t0 = Environment.TickCount64;
        // Keep answering pings on the live socket while the dead one goes silent.
        bool deadClosed = _f.Until(() => {
            DrainAndPong(alive);
            return dead.IsTcpClosed();
        }, 4000);
        long elapsed = Environment.TickCount64 - t0;
        Assert.That(deadClosed, Is.True, "unresponsive socket closed");
        Assert.That(elapsed, Is.GreaterThanOrEqualTo(300).And.LessThan(2500),
            "closed after ~2 missed intervals (" + elapsed + " ms)");
        Assert.That(WaitSeen("disconnected", did), Is.True, "player_disconnected from heartbeat");

        long keepUntil = Environment.TickCount64 + 700;
        while (Environment.TickCount64 < keepUntil) {
            DrainAndPong(alive); // keep answering pings so the live socket survives
            System.Threading.Thread.Sleep(30);
        }
        Assert.That(alive.IsTcpClosed(), Is.False, "pong-answering socket stays open");
        Assert.That(_f.Until(() => Host.GetPlayer(aid).RttMs > 0f), Is.True,
            "rtt_ms measured from heartbeat pong");
        alive.Dispose();
        dead.Dispose();
    }

    // Answers any pending pings with pongs. Bounded — must not outlive the caller's Until deadline.
    private static void DrainAndPong(TestWs ws) {
        for (int i = 0; i < 8; i++) {
            try {
                var f = ws.Recv(25);
                if (f.Op == 9) ws.Send(10, f.Payload);
            } catch (Exception) {
                return;
            }
        }
    }

    [Test]
    public void StopClosesClients() {
        var ws = Join();
        int id = (int)ws.LastWelcome["id"];
        bool stopped = false;
        Host.Stopped += () => stopped = true;
        Host.Stop();
        Assert.That(stopped, Is.True, "stopped signal");
        Assert.That(Host.Running, Is.False);
        Assert.That(ws.RecvClose(), Is.EqualTo(1001), "clients get 1001 on stop");
        Assert.That(Host.Players().Count, Is.EqualTo(0), "players cleared on stop");
        Assert.That(Host.GetPlayer(id), Is.Null);
        ws.Dispose();
    }
}
