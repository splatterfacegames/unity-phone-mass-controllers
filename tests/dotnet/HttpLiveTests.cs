// Port of test_http.gd — GET/HEAD, keep-alive, pipelining, status codes, traversal, MIME,
// ranges, symlink confinement, slowloris timeouts, garbage input, large-file streaming.
// Everything runs against a real PmcHostCore on 127.0.0.1.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Splatter.Pmc;

[TestFixture]
internal sealed class HttpLiveTests {
    private string _root;
    private string _www;
    private HostFixture _f;
    private int Port { get { return _f.Port; } }

    [SetUp]
    public void SetUp() {
        _root = Path.Combine(Path.GetTempPath(), "pmc-http-" + Guid.NewGuid().ToString("N"));
        _www = Path.Combine(_root, "www");
        Write(_www, "index.html", "<!doctype html><title>hi</title>");
        Write(_www, "style.css", "body{}");
        Write(_www, "app.js", "export const x = 1;");
        Write(_www, "data.json", "{\"a\":1}");
        Write(_www, "mod.wasm", new byte[] { 0, 97, 115, 109 });
        Write(_www, "pic.png", new byte[] { 137, 80, 78, 71 });
        Write(_www, "blob.xyz", "?");
        Write(_www, "hello world.txt", "spaced");
        Write(_www, "sub/index.html", "sub index");
        Write(_www, "sub/deep.txt", "deep");
        Write(_root, "secret.txt", "TOP SECRET");
        Write(Path.Combine(_root, "extra"), "asset.txt", "extra asset");
        _f = HostFixture.Start(cfg => {
            cfg.ControllerDir = _www;
            cfg.HeaderTimeoutSeconds = 0.6f;
            cfg.MaxConnectionsPerAddress = 0; // this suite opens many loopback sockets
        });
    }

    [TearDown]
    public void TearDown() {
        _f?.Dispose();
        try { Directory.Delete(_root, true); } catch (Exception) { }
    }

    private static void Write(string dir, string name, string content) {
        Write(dir, name, Encoding.UTF8.GetBytes(content));
    }

    private static void Write(string dir, string name, byte[] content) {
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllBytes(path, content);
    }

    private HttpResp Get(string path, string headers = "") {
        using (var s = new TestHttp(Port)) {
            return s.Exchange("GET " + path + " HTTP/1.1\r\nHost: x\r\nConnection: close\r\n" + headers + "\r\n");
        }
    }

    [Test]
    public void GetHeadBasics() {
        var r = Get("/");
        Assert.That(r.Status, Is.EqualTo(200), "GET / status");
        Assert.That(r.BodyText(), Is.EqualTo("<!doctype html><title>hi</title>"), "index.html body");
        Assert.That(r.Header("content-type"), Is.EqualTo("text/html; charset=utf-8"));
        Assert.That(r.Header("cache-control"), Is.EqualTo("no-cache"), "html no-cache");
        Assert.That(r.Header("connection"), Is.EqualTo("close"), "Connection: close honoured");

        using (var s = new TestHttp(Port)) {
            s.Send("HEAD /style.css HTTP/1.1\r\nHost: x\r\n\r\n");
            var rh = s.Recv();
            Assert.That(rh.Status, Is.EqualTo(200), "HEAD status");
            Assert.That(rh.Header("content-length"), Is.EqualTo("6"), "HEAD content-length of full body");
            Assert.That(rh.Body.Length, Is.EqualTo(0), "HEAD sends no body");
            Assert.That(rh.Header("connection"), Is.EqualTo("keep-alive"), "keep-alive default for 1.1");
            // Same connection: a GET right after must parse cleanly.
            var rg = s.Exchange("GET /style.css HTTP/1.1\r\nHost: x\r\n\r\n");
            Assert.That(rg.BodyText(), Is.EqualTo("body{}"), "GET after HEAD on same socket");
        }

        r = Get("/sub");
        Assert.That(r.Status, Is.EqualTo(301), "directory without slash redirects");
        Assert.That(r.Header("location"), Is.EqualTo("/sub/"), "redirect location");
        Assert.That(Get("/sub/").BodyText(), Is.EqualTo("sub index"), "directory index");
        Assert.That(Get("/sub/deep.txt?x=1#frag").BodyText(), Is.EqualTo("deep"), "query ignored");
        Assert.That(Get("/hello%20world.txt").BodyText(), Is.EqualTo("spaced"), "percent-decoded name");
        Assert.That(Get("/pmc/healthz").Status, Is.EqualTo(200), "healthz");

        r = Get("/pmc/info.json");
        Assert.That(r.Status, Is.EqualTo(200), "info.json");
        var info = JObject.Parse(r.BodyText());
        Assert.That((int)info["sdk"], Is.EqualTo(1));
        Assert.That(info["join_url"], Is.Not.Null);
        Assert.That((bool)info["code_required"], Is.False);

        Assert.That(Get("/pmc/").Status, Is.EqualTo(404), "/pmc/ has no index");

        // /pmc/qr.png: 501 without a provider, 200 + image/png with one.
        Assert.That(Get("/pmc/qr.png").Status, Is.EqualTo(501), "qr.png 501 without a provider");
        var dark = new bool[21, 21];
        for (int y = 0; y < 21; y++) dark[y, y] = true;
        PmcHostCore.QrPngProvider = (url, px, quiet) => PmcPng.EncodeBitmap(dark, px, quiet);
        try {
            r = Get("/pmc/qr.png");
            Assert.That(r.Status, Is.EqualTo(200), "qr.png 200 with a provider");
            Assert.That(r.Header("content-type"), Is.EqualTo("image/png"));
            var img = PngCheck.Decode(r.Body);
            Assert.That(img[0], Is.EqualTo((21 + 8) * 8), "qr.png width with quiet=4, px=8");
            Assert.That(img[1], Is.EqualTo((21 + 8) * 8), "qr.png height");
        } finally {
            PmcHostCore.QrPngProvider = null;
        }

        // HTTP/1.0 without Host works and closes by default.
        using (var s10 = new TestHttp(Port)) {
            var r10 = s10.Exchange("GET /style.css HTTP/1.0\r\n\r\n");
            Assert.That(r10.Status, Is.EqualTo(200), "HTTP/1.0 without Host works");
            Assert.That(s10.WaitClosed(3000), Is.True, "HTTP/1.0 closes by default");
        }
    }

    [Test]
    public void PmcJsServedWhenWebDirSet() {
        // Locate the package Web/ dir relative to the test assembly.
        string dir = AppContext.BaseDirectory;
        string web = null;
        for (int i = 0; i < 12 && dir != null; i++) {
            string cand = Path.Combine(dir, "Packages", "com.splatterfacegames.phone-mass-controllers", "Web");
            if (File.Exists(Path.Combine(cand, "pmc.js"))) { web = cand; break; }
            dir = Path.GetDirectoryName(dir);
        }
        if (web == null) {
            Assert.That(Get("/pmc/pmc.js").Status, Is.EqualTo(404), "pmc.js 404 while the SDK file is absent");
            return;
        }
        _f.Host.WebDir = web;
        var r = Get("/pmc/pmc.js");
        Assert.That(r.Status, Is.EqualTo(200), "/pmc/pmc.js served");
        Assert.That(r.Header("content-type"), Is.EqualTo("text/javascript; charset=utf-8"), "pmc.js mime");
    }

    [Test]
    public void KeepAlivePipelining() {
        using (var s = new TestHttp(Port)) {
            var names = new[] { "style.css", "app.js", "sub/deep.txt", "data.json", "nope.txt", "style.css" };
            var batch = new StringBuilder();
            foreach (var n in names) {
                batch.Append("GET /").Append(n).Append(" HTTP/1.1\r\nHost: x\r\n\r\n");
            }
            s.Send(batch.ToString());
            var expect = new string[] { "body{}", "export const x = 1;", "deep", "{\"a\":1}", null, "body{}" };
            for (int i = 0; i < names.Length; i++) {
                var r = s.Recv();
                if (expect[i] == null) {
                    Assert.That(r.Status, Is.EqualTo(404), "pipelined 404 in order");
                } else {
                    Assert.That(r.BodyText(), Is.EqualTo(expect[i]), "pipelined response " + i + " in order");
                }
            }
            // Split a request across several sends.
            s.Send("GET /sub/de");
            System.Threading.Thread.Sleep(60);
            s.Send("ep.txt HTTP/1.1\r\nHo");
            System.Threading.Thread.Sleep(60);
            s.Send("st: x\r\n\r\n");
            var r2 = s.Recv();
            Assert.That(r2.BodyText(), Is.EqualTo("deep"), "request split across packets");
            Assert.That(s.Closed, Is.False, "connection kept alive");
        }
    }

    [Test]
    public void StatusCodes() {
        var cases = new[] {
            new[] { "GET /does-not-exist HTTP/1.1\r\nHost: x\r\n\r\n", "404", "unknown path" },
            new[] { "POST /style.css HTTP/1.1\r\nHost: x\r\nContent-Length: 0\r\n\r\n", "405", "POST static file" },
            new[] { "DELETE / HTTP/1.1\r\nHost: x\r\n\r\n", "405", "DELETE" },
            new[] { "POST /nope HTTP/1.1\r\nHost: x\r\nContent-Length: 3\r\n\r\nabc", "405", "POST unknown path" },
            new[] { "POST /x HTTP/1.1\r\nHost: x\r\nContent-Length: 999999\r\n\r\n", "413", "body too large" },
            new[] { "POST /x HTTP/1.1\r\nHost: x\r\nTransfer-Encoding: chunked\r\n\r\n", "501", "chunked request body" },
            new[] { "HELLO\r\n\r\n", "400", "garbage request line" },
            new[] { "GET /%zz HTTP/1.1\r\nHost: x\r\n\r\n", "400", "bad percent escape" },
            new[] { "GET /%00 HTTP/1.1\r\nHost: x\r\n\r\n", "400", "encoded NUL" },
            new[] { "GET / HTTP/1.1\r\n\r\n", "400", "missing Host" },
            new[] { "GET / HTTP/2.0\r\nHost: x\r\n\r\n", "505", "HTTP/2.0" },
            new[] { "GET / FOO/1.1\r\nHost: x\r\n\r\n", "400", "bad protocol" },
            new[] { "GET relative HTTP/1.1\r\nHost: x\r\n\r\n", "400", "non-origin target" },
            new[] { "GET / HTTP/1.1\r\nHost: x\r\nBad Header: y\r\n\r\n", "400", "space in header name" },
            new[] { "GET / HTTP/1.1\r\nHost: x\r\nContent-Length: -1\r\n\r\n", "400", "negative Content-Length" },
            new[] { "GET / HTTP/1.1\r\nHost: x\r\nContent-Length: 1\r\nContent-Length: 2\r\n\r\n", "400", "conflicting Content-Length" },
            new[] { "GET / HTTP/1.1\r\nHost: x\r\n folded\r\n\r\n", "400", "obsolete folding" },
            new[] { "GET /pmc/ws HTTP/1.1\r\nHost: x\r\n\r\n", "426", "ws path without upgrade" },
            new[] { "GET /pmc/ws HTTP/1.1\r\nHost: x\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Version: 8\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n\r\n", "426", "ws version 8" },
            new[] { "GET /pmc/ws HTTP/1.1\r\nHost: x\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Version: 13\r\nSec-WebSocket-Key: short\r\n\r\n", "400", "bad ws key" },
            new[] { "POST /pmc/ws HTTP/1.1\r\nHost: x\r\nContent-Length: 0\r\n\r\n", "405", "POST ws" },
        };
        foreach (var c in cases) {
            using (var s = new TestHttp(Port)) {
                var r = s.Exchange(c[0]);
                Assert.That(r.Status, Is.EqualTo(int.Parse(c[1])), c[2]);
            }
        }

        using (var s2 = new TestHttp(Port)) {
            var r2 = s2.Exchange("GET / HTTP/1.1\r\nHost: x\r\nX-Pad: " + new string('a', 17000) + "\r\n\r\n");
            Assert.That(r2.Status, Is.EqualTo(431), "header > 16 KiB");
            Assert.That(s2.WaitClosed(3000), Is.True, "closed after 431");
        }
        using (var s3 = new TestHttp(Port)) {
            var r3 = s3.Exchange("GET / HTTP/1.1\r\nHost: x\r\nX-Pad: " + new string('a', 15000) + "\r\n\r\n");
            Assert.That(r3.Status, Is.EqualTo(200), "15 KiB header accepted");
        }
    }

    [Test]
    public void PathTraversalBlocked() {
        var attacks = new[] {
            "/../secret.txt", "/%2e%2e/secret.txt", "/%2E%2E/secret.txt", "/..%2fsecret.txt",
            "/%2e%2e%2fsecret.txt", "/sub/../../secret.txt", "/sub/%2e%2e/%2e%2e/secret.txt",
            "/.%2e/secret.txt", "/%2e./secret.txt", "/..%5csecret.txt", "/%5c..%5csecret.txt",
            "/sub/..%5c..%5csecret.txt", "/..\\secret.txt", "/sub\\..\\..\\secret.txt",
            "/%2e%2e%20/secret.txt", "/...%2fsecret.txt", "/..%2e/secret.txt",
            "/%252e%252e/secret.txt", "//../secret.txt", "/./.././secret.txt",
            "/sub/.%2E/..%2Fsecret.txt", "/..;/secret.txt", "/C:/Windows/win.ini",
            "/c%3a/Windows/win.ini", "/%c0%ae%c0%ae/secret.txt", "/..%00/secret.txt",
            "/sub/deep.txt%00.html", "/.. /secret.txt", "/..%09/secret.txt",
        };
        foreach (var a in attacks) {
            var r = Get(a);
            bool leaked = r.BodyText().Contains("TOP SECRET");
            Assert.That(leaked, Is.False, "leaked for " + a);
            Assert.That(r.Status, Is.InRange(400, 499),
                "traversal blocked: " + a + " (status " + r.Status + ")");
        }
        // Absolute-form target is reduced to its path.
        var abs = Get("http://evil.example/../secret.txt");
        Assert.That(abs.Status, Is.GreaterThanOrEqualTo(400), "absolute-form traversal blocked");
        Assert.That(abs.BodyText().Contains("TOP SECRET"), Is.False);
    }

    [Test]
    public void ResolveRules() {
        string www = _www.Replace('\\', '/');
        Assert.That(PmcStaticFiles.Resolve(www, "a/b.txt"), Is.EqualTo(www + "/a/b.txt"), "resolve joins");
        Assert.That(PmcStaticFiles.Resolve(www + "/", "/a//./b/"), Is.EqualTo(www + "/a/b/"), "resolve normalises");
        Assert.That(PmcStaticFiles.Resolve(www, "../x"), Is.EqualTo(""), "resolve refuses ..");
        Assert.That(PmcStaticFiles.Resolve(www, "a/..."), Is.EqualTo(""), "resolve refuses trailing dots");
    }

    [Test]
    public void MimeTable() {
        var cases = new Dictionary<string, string> {
            { "/style.css", "text/css; charset=utf-8" }, { "/app.js", "text/javascript; charset=utf-8" },
            { "/data.json", "application/json; charset=utf-8" }, { "/mod.wasm", "application/wasm" },
            { "/pic.png", "image/png" }, { "/blob.xyz", "application/octet-stream" },
            { "/sub/deep.txt", "text/plain; charset=utf-8" },
        };
        foreach (var kv in cases) {
            var r = Get(kv.Key);
            Assert.That(r.Header("content-type"), Is.EqualTo(kv.Value), "content-type of " + kv.Key);
        }
        Assert.That(Get("/app.js").Header("cache-control"), Is.EqualTo("no-cache"), "js no-cache");
        Assert.That(Get("/pic.png").Header("cache-control"), Is.Null, "png cacheable");
    }

    [Test]
    public void RoutesAndMounts() {
        _f.Host.AddRoute("/api/", req => {
            if (req.SubPath() == "echo") {
                return PmcHttpResponse.Json(new JObject {
                    ["method"] = req.Method,
                    ["body"] = req.BodyText(),
                    ["q"] = JObject.FromObject(req.Query),
                });
            }
            if (req.SubPath() == "bad") {
                return (PmcHttpResponse)null; // handler returning null falls through to static
            }
            return null;
        });
        _f.Host.AddRoute("/api/deeper/", req => PmcHttpResponse.Text("deeper"));
        using (var s = new TestHttp(Port)) {
            var r = s.Exchange("POST /api/echo?a=1&b=x%20y+z HTTP/1.1\r\nHost: x\r\nContent-Length: 5\r\n\r\nhello");
            var d = JObject.Parse(r.BodyText());
            Assert.That((string)d["method"], Is.EqualTo("POST"));
            Assert.That((string)d["body"], Is.EqualTo("hello"));
            Assert.That((string)d["q"]["a"], Is.EqualTo("1"));
            Assert.That((string)d["q"]["b"], Is.EqualTo("x y z"), "query + and %20 decode");
        }
        Assert.That(Get("/api/deeper/x").BodyText(), Is.EqualTo("deeper"), "longest prefix wins");
        Assert.That(Get("/api/none").Status, Is.EqualTo(404), "null falls through to static (404)");

        _f.Host.ServeDirectory("/assets", Path.Combine(_root, "extra"));
        Assert.That(Get("/assets/asset.txt").BodyText(), Is.EqualTo("extra asset"), "serve_directory");
        var r2 = Get("/assets/../secret.txt");
        Assert.That(r2.Status, Is.GreaterThanOrEqualTo(400), "serve_directory traversal blocked");
        var r3 = Get("/assets/%2e%2e/secret.txt");
        Assert.That(r3.Status, Is.GreaterThanOrEqualTo(400), "serve_directory encoded traversal blocked");
        Assert.That(r3.BodyText().Contains("SECRET"), Is.False);
        _f.Host.RemoveRoute("/api/");
        Assert.That(Get("/api/echo").Status, Is.EqualTo(404), "remove_route");
    }

    [Test]
    public void RouteHandlerThrowIs500() {
        _f.Host.AddRoute("/boom/", req => { throw new InvalidOperationException("x"); });
        Assert.That(Get("/boom/now").Status, Is.EqualTo(500), "handler throw -> 500");
    }

    [Test]
    public void SymlinkConfinement() {
        string link = Path.Combine(_www, "evil.txt");
        try {
            File.CreateSymbolicLink(link, Path.Combine(_root, "secret.txt"));
        } catch (Exception e) {
            Assert.Ignore("symlink creation failed (" + e.GetType().Name + ") — needs privilege");
            return;
        }
        var r = Get("/evil.txt");
        Assert.That(r.Status, Is.EqualTo(403), "symlink to outside file refused");
        Assert.That(r.BodyText().Contains("TOP SECRET"), Is.False, "no secret bytes leaked");

        string dirLink = Path.Combine(_www, "updir");
        try {
            Directory.CreateSymbolicLink(dirLink, Path.Combine(_root, "extra"));
        } catch (Exception) {
            dirLink = null;
        }
        if (dirLink != null) {
            Assert.That(Get("/updir/asset.txt").Status, Is.EqualTo(403), "symlinked dir outside refused");
        }
        string inner = Path.Combine(_www, "inner.txt");
        try {
            File.CreateSymbolicLink(inner, Path.Combine(_www, "sub", "deep.txt"));
        } catch (Exception) {
            inner = null;
        }
        if (inner != null) {
            var ri = Get("/inner.txt");
            Assert.That(ri.Status, Is.EqualTo(200), "symlink inside the tree still served (status)");
            Assert.That(ri.BodyText(), Is.EqualTo("deep"), "symlink inside the tree still served");
        }
    }

    [Test]
    public void OriginCheck() {
        _f.Host.CheckOrigin = true;
        _f.Host.AllowedOrigins.Add("http://ok.example");
        using (var bad = new TestHttp(Port)) {
            var r = bad.Exchange("GET /pmc/ws HTTP/1.1\r\nHost: x\r\nUpgrade: websocket\r\n"
                + "Connection: Upgrade\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n"
                + "Sec-WebSocket-Version: 13\r\nOrigin: http://evil.example\r\n\r\n");
            Assert.That(r.Status, Is.EqualTo(403), "disallowed origin refused");
        }
        using (var ws = new TestWs(Port)) {
            ws.Upgrade("/pmc/ws", "Origin: http://ok.example\r\n");
        }
        using (var bare = new TestWs(Port)) {
            bare.Upgrade(); // no Origin header still allowed (non-browser client)
        }
    }

    [Test]
    public void ByteRanges() {
        var r = Get("/app.js", "Range: bytes=7-11\r\n");
        Assert.That(r.Status, Is.EqualTo(206), "206");
        Assert.That(r.BodyText(), Is.EqualTo("const"), "range body");
        Assert.That(r.Header("content-range"), Is.EqualTo("bytes 7-11/19"), "content-range");
        Assert.That(Get("/app.js", "Range: bytes=-2\r\n").BodyText(), Is.EqualTo("1;"), "suffix range");
        Assert.That(Get("/app.js", "Range: bytes=100-\r\n").Status, Is.EqualTo(416), "unsatisfiable");
        Assert.That(Get("/app.js", "Range: bytes=0-1,3-4\r\n").Status, Is.EqualTo(200), "multi-range ignored");
    }

    [Test]
    public void SlowlorisHeaderTimeout() {
        using (var s = new TestHttp(Port)) {
            s.Send("GET / HTTP/1.1\r\nHost: x\r\n");
            System.Threading.Thread.Sleep(300);
            s.Send("X-Slow: 1\r\n");
            var r = s.Recv(4000);
            Assert.That(r.Status, Is.EqualTo(408), "408 after header timeout");
            Assert.That(s.WaitClosed(3000), Is.True, "slow connection closed");
        }
        using (var idle = new TestHttp(Port)) {
            Assert.That(idle.WaitClosed(4000), Is.True, "idle connection closed silently");
        }
        using (var trickle = new TestHttp(Port)) {
            long start = Environment.TickCount64;
            int sent = 0;
            while (!trickle.Closed && Environment.TickCount64 - start < 4000) {
                try { trickle.Send("X"); sent += 1; } catch (Exception) { break; }
                System.Threading.Thread.Sleep(80);
                trickle.Pump(10);
            }
            Assert.That(trickle.Closed, Is.True, "trickling connection closed (sent " + sent + " bytes)");
        }
        Assert.That(Get("/style.css").Status, Is.EqualTo(200), "host still serves");
    }

    [Test]
    public void GarbageInput() {
        var rng = new Random(1234);
        for (int round = 0; round < 5; round++) {
            var junk = new byte[20000 + round * 5000];
            rng.NextBytes(junk);
            using (var s = new TestHttp(Port)) {
                s.Send(junk);
                Assert.That(s.WaitClosed(4000), Is.True, "garbage round " + round + " closed");
            }
        }
        using (var s = new TestHttp(Port)) {
            s.Send(new byte[] { 0, 0, 0, 13, 10, 13, 10 });
            Assert.That(s.WaitClosed(4000), Is.True, "NUL request closed");
        }
        var many = new List<TestHttp>();
        try {
            for (int i = 0; i < 50; i++) {
                var s = new TestHttp(Port);
                s.Send("GET / HTTP/1.1\r\nHo");
                many.Add(s);
            }
            Assert.That(Get("/style.css").Status, Is.EqualTo(200), "serves while 50 partial requests pending");
        } finally {
            foreach (var s in many) s.Dispose();
        }
    }

    [Test]
    public void LargeFileStreaming() {
        int size = 5 * 1024 * 1024 + 123;
        var data = new byte[size];
        var rng = new Random(42);
        rng.NextBytes(data);
        File.WriteAllBytes(Path.Combine(_www, "big.bin"), data);
        string expected = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

        var r = Get("/big.bin");
        Assert.That(r.Status, Is.EqualTo(200), "large file status");
        Assert.That(r.Body.Length, Is.EqualTo(size), "large file size");
        Assert.That(Convert.ToHexString(SHA256.HashData(r.Body)).ToLowerInvariant(),
            Is.EqualTo(expected), "large file sha256 intact");

        // Two concurrent downloads: full + range.
        using (var a = new TestHttp(Port))
        using (var b = new TestHttp(Port)) {
            a.Send("GET /big.bin HTTP/1.1\r\nHost: x\r\n\r\n");
            b.Send("GET /big.bin HTTP/1.1\r\nHost: x\r\nRange: bytes=1000000-1999999\r\n\r\n");
            var ra = a.Recv(60000);
            var rb = b.Recv(60000);
            Assert.That(Convert.ToHexString(SHA256.HashData(ra.Body)).ToLowerInvariant(),
                Is.EqualTo(expected), "concurrent full download intact");
            var slice = new byte[1000000];
            Buffer.BlockCopy(data, 1000000, slice, 0, 1000000);
            Assert.That(Convert.ToHexString(SHA256.HashData(rb.Body)).ToLowerInvariant(),
                Is.EqualTo(Convert.ToHexString(SHA256.HashData(slice)).ToLowerInvariant()),
                "concurrent range download intact");
        }
    }
}

/// <summary>Minimal PNG reader: signature, IHDR dims, IDAT inflate, per-scanline filter 0.</summary>
internal static class PngCheck {
    /// <summary>Returns {width, height} and throws unless the PNG decodes cleanly.</summary>
    public static int[] Decode(byte[] png) {
        Assert.That(png.Length, Is.GreaterThan(33), "png too small");
        var sig = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
        for (int i = 0; i < 8; i++) Assert.That(png[i], Is.EqualTo(sig[i]), "png signature");
        int w = BE32(png, 16);
        int h = BE32(png, 20);
        Assert.That(w, Is.GreaterThan(0));
        Assert.That(h, Is.GreaterThan(0));
        // Collect IDAT payloads.
        var idat = new MemoryStream();
        int off = 8;
        bool iend = false;
        while (off + 8 <= png.Length) {
            int len = BE32(png, off);
            string type = Encoding.ASCII.GetString(png, off + 4, 4);
            if (type == "IDAT") idat.Write(png, off + 8, len);
            if (type == "IEND") { iend = true; break; }
            off += 12 + len;
        }
        Assert.That(iend, Is.True, "IEND present");
        idat.Position = 0;
        using (var z = new System.IO.Compression.ZLibStream(idat, System.IO.Compression.CompressionMode.Decompress))
        using (var outp = new MemoryStream()) {
            z.CopyTo(outp);
            var raw = outp.ToArray();
            Assert.That(raw.Length, Is.GreaterThanOrEqualTo((w + 1) * h), "scanline payload size");
        }
        return new[] { w, h };
    }

    private static int BE32(byte[] b, int off) {
        return (b[off] << 24) | (b[off + 1] << 16) | (b[off + 2] << 8) | b[off + 3];
    }
}
