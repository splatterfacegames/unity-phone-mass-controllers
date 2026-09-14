using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Splatter.Pmc;

namespace Splatter.Pmc.Core.Tests
{
    /// <summary>
    /// PmcTunnel against the fake cloudflared fixture (tests/fixtures/tunnel) that replays
    /// real-looking logs: success, slow URL, no URL ever (timeout), error exit, exit after ready,
    /// named modes, 429 retries, QUIC fallback, lost/recovered registrations, config isolation,
    /// stale pid reaping. Port of the Godot test_tunnel.gd suite.
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class TunnelTests
    {
        string _fake;
        readonly System.Collections.Generic.List<string> _dirs = new System.Collections.Generic.List<string>();

        [SetUp]
        public void SetUp()
        {
            _fake = Fx.Fake();
            Fx.ClearEnv();
        }

        [TearDown]
        public void TearDown()
        {
            Fx.ClearEnv();
            foreach (var d in _dirs)
                Fx.RemoveDir(d);
            _dirs.Clear();
        }

        Rec Make(string bin)
        {
            var r = new Rec(bin);
            _dirs.Add(r.Dir);
            return r;
        }

        // ---------------------------------------------------------------- statics

        [Test]
        public void ParsingAndResolution()
        {
            Assert.That(PmcTunnel.ParseUrl("2026-09-14T09:00:01Z INF |  https://random-words-here.trycloudflare.com                   |"),
                Is.EqualTo("https://random-words-here.trycloudflare.com"));
            Assert.That(PmcTunnel.ParseUrl("ERR Failed to request quick Tunnel: Post \"https://api.trycloudflare.com/tunnel\": i/o timeout"),
                Is.EqualTo(""), "the API hostname is not a tunnel URL");
            Assert.That(PmcTunnel.ParseUrl("INF Requesting new quick Tunnel on trycloudflare.com..."), Is.EqualTo(""));
            Assert.That(PmcTunnel.AssetName(), Is.Not.EqualTo(""), "release asset known for this OS/arch");
            Assert.That(PmcTunnel.InstallPath().EndsWith(PmcTunnel.ExecutableName()), Is.True);
            Assert.That(PmcTunnel.ResolveBinary("C:/definitely/not/here/cloudflared.exe"), Is.EqualTo(""));
            Assert.That(PmcTunnel.UnsupportedReason(), Is.EqualTo(""), "desktop platform supports subprocesses");

            var tmp = Path.Combine(Fx.NewTempDir(), "abc.txt");
            _dirs.Add(Path.GetDirectoryName(tmp));
            File.WriteAllText(tmp, "abc");
            Assert.That(PmcTunnel.FileSha256(tmp),
                Is.EqualTo("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"), "SHA-256 of a file");
            Assert.That(PmcTunnel.FileSha256(tmp + ".missing"), Is.EqualTo(""));
            Assert.That(PmcTunnel.ResolveBinary(_fake), Is.EqualTo(Path.GetFullPath(_fake)), "explicit path resolves");
        }

        [Test]
        public void QuicErrorDetection()
        {
            Assert.That(PmcTunnel.IsQuicError("ERR Failed to create new quic connection error=\"timeout: no recent network activity\" connIndex=0"), Is.True);
            Assert.That(PmcTunnel.IsQuicError("... ERR Serve tunnel error error=\"timeout: no recent network activity\""), Is.True);
            // the Godot bug this guards: "QuickTunnel" contains "quic" — a 429 line must NOT match
            Assert.That(PmcTunnel.IsQuicError(
                "2026-09-14T09:00:01Z ERR Error unmarshaling QuickTunnel response: error code: 1015 error=\"invalid character\" status_code=\"429 Too Many Requests\""),
                Is.False);
            Assert.That(PmcTunnel.IsQuicError("INF normal progress line"), Is.False);
        }

        [Test]
        public void BinaryVersionAndVerification()
        {
            Assert.That(PmcTunnel.BinaryVersion(_fake), Is.EqualTo("2025.8.1"), "fake --version parses");
            Assert.That(PmcTunnel.VersionAtLeast("2025.8.1", "2022.6.2"), Is.True);
            Assert.That(PmcTunnel.VersionAtLeast("2022.6.2", "2025.8.1"), Is.False);
            Assert.That(PmcTunnel.VersionAtLeast("2025.8", "2025.8.0"), Is.True, "missing patch counts as 0");
            Assert.That(PmcTunnel.VerifyBinary(_fake, "2022.6.2"), Is.EqualTo(""), "fake passes the minimum");
            Assert.That(PmcTunnel.VerifyBinary(_fake, "2999.1.1"), Is.Not.EqualTo(""), "too-old binary rejected");
            Assert.That(PmcTunnel.VerifyBinary(_fake + ".missing"), Is.Not.EqualTo(""), "unrunnable binary rejected");
        }

        // ---------------------------------------------------------------- lifecycle

        [Test]
        public void MissingBinaryWithoutDownloadFails()
        {
            Fx.Mode("ok");
            using (var r = Make("C:/definitely/not/here/cloudflared.exe"))
            {
                var code = r.T.Start(8080);
                r.T.Pump();
                Assert.That(code, Is.EqualTo(PmcTunnel.ErrNoBinary));
                Assert.That(r.States.ToArray(), Is.EqualTo(new[] { "failed" }), "fails immediately");
                Assert.That(r.Details[0], Does.Contain("not found"));
            }
        }

        [Test]
        public void QuickHappyPath()
        {
            Fx.Mode("ok");
            using (var r = Make(_fake))
            {
                var code = r.T.Start(8080);
                Assert.That(code, Is.EqualTo(PmcTunnel.Ok));
                r.T.Pump();
                Assert.That(r.States.ToArray(), Is.EqualTo(new[] { "starting" }), "starting right away");
                Assert.That(r.T.IsRunning(), Is.True, "process running");
                Assert.That(r.WaitFor("ready", 15), Is.True, "ready (log: " + r.LogJoined + ")");
                Assert.That(r.T.Url, Is.EqualTo("https://random-words-here.trycloudflare.com"));
                Assert.That(r.Details[r.Details.Count - 1], Is.EqualTo("https://random-words-here.trycloudflare.com"), "ready detail is the URL");
                var pid = r.T.Pid;
                Assert.That(pid, Is.GreaterThan(0));
                r.T.Stop();
                r.WaitUntil(() => r.States.Count >= 3, 3);
                Assert.That(r.States.ToArray(), Is.EqualTo(new[] { "starting", "ready", "stopped" }));
                Assert.That(r.T.Url, Is.EqualTo(""));
                Assert.That(r.WaitUntil(() => !Fx.ProcAlive(pid), 5), Is.True, "process killed on stop");
            }
        }

        [Test]
        public void QuickHappyPathWithDnsProbe()
        {
            // VerifyDns on, resolver seam answers true — ready only after the probe confirms.
            Fx.Mode("ok");
            string probedHost = null;
            using (var r = Make(_fake))
            {
                r.T.VerifyDns = true;
                r.T.DnsProbe = (host, ct) =>
                {
                    probedHost = host;
                    return Task.FromResult(true);
                };
                r.T.Start(8080);
                Assert.That(r.WaitFor("ready", 15), Is.True, "ready after DNS-ok (log: " + r.LogJoined + ")");
                Assert.That(probedHost, Is.EqualTo("random-words-here.trycloudflare.com"), "probe got the tunnel hostname");
                Assert.That(r.LogJoined, Does.Contain("waiting for"));
                r.T.Stop();
            }
        }

        [Test]
        public void DnsResolvesOverLocalDohEndpoint()
        {
            // Exercises the real HttpClient DoH path against a loopback dns-json responder.
            Fx.Mode("ok");
            using (var doh = new TinyHttp("{\"Status\":0,\"Answer\":[{\"name\":\"x\",\"type\":1,\"TTL\":120,\"data\":\"1.2.3.4\"}]}", "application/dns-json"))
            using (var r = Make(_fake))
            {
                r.T.VerifyDns = true;
                r.T.DnsOverHttpsUrl = "http://127.0.0.1:" + doh.Port + "/dns-query";
                r.T.Start(8080);
                Assert.That(r.WaitFor("ready", 20), Is.True, "ready after DoH answer (log: " + r.LogJoined + ")");
                Assert.That(r.LogJoined, Does.Contain("DNS resolves after"));
                r.T.Stop();
            }
        }

        [Test]
        public void DnsTimeoutFallsBackToReady()
        {
            Fx.Mode("ok");
            using (var r = Make(_fake))
            {
                r.T.VerifyDns = true;
                r.T.DnsTimeoutSec = 2f;
                r.T.DnsOverHttpsUrl = "http://127.0.0.1:9/dns-query"; // discard port: never answers
                r.T.Start(8087);
                Assert.That(r.WaitUntil(() => r.T.LogLines.Any(l => l.Contains("waiting for")), 15), Is.True);
                r.WaitUntil(() => false, 1.0); // keep pumping a beat
                Assert.That(r.T.State, Is.EqualTo("starting"), "not ready while DNS is unconfirmed");
                Assert.That(r.WaitFor("ready", 15), Is.True, "ready after the DNS timeout");
                Assert.That(r.LogJoined, Does.Contain("DNS check timed out"));
                r.T.Stop();
            }
        }

        [Test]
        public void SlowUrl()
        {
            Fx.Mode("slow");
            using (var r = Make(_fake))
            {
                var t0 = DateTime.UtcNow;
                r.T.Start(8081);
                r.WaitUntil(() => false, 1.5);
                Assert.That(r.T.State, Is.EqualTo("starting"), "still starting after 1.5 s");
                Assert.That(r.WaitFor("ready", 20), Is.True, "eventually ready (log: " + r.LogJoined + ")");
                Assert.That((DateTime.UtcNow - t0).TotalMilliseconds, Is.GreaterThanOrEqualTo(2500), "ready only after the delayed URL");
                r.T.Stop();
            }
        }

        [Test]
        public void NoUrlTimesOut()
        {
            Fx.Mode("no_url");
            using (var r = Make(_fake))
            {
                r.T.ReadyTimeoutSec = 3f;
                r.T.Start(8082);
                r.WaitUntil(() => false, 0.5);
                var pid = r.T.Pid;
                Assert.That(r.WaitFor("failed", 15), Is.True, "fails on timeout");
                var reason = r.Details[r.Details.Count - 1];
                Assert.That(reason, Does.Contain("timed out"));
                Assert.That(reason, Does.Contain("api.trycloudflare.com"));
                Assert.That(r.T.Url, Is.EqualTo(""));
                Assert.That(r.WaitUntil(() => !Fx.ProcAlive(pid), 5), Is.True, "process killed after timeout");
            }
        }

        [Test]
        public void ErrorExitFails()
        {
            Fx.Mode("error_exit");
            using (var r = Make(_fake))
            {
                r.T.MaxRetries = 0;
                r.T.Start(8083);
                Assert.That(r.WaitFor("failed", 15), Is.True, "fails when the process exits");
                var why = r.Details[r.Details.Count - 1];
                Assert.That(why, Does.Contain("exited"));
                Assert.That(why, Does.Contain("code 1"));
                Assert.That(why, Does.Contain("429"));
                Assert.That(r.States.ToArray(), Is.EqualTo(new[] { "starting", "failed" }));
            }
        }

        [Test]
        public void ExitAfterReadyBecomesLost()
        {
            Fx.Mode("exit_after_ready");
            using (var r = Make(_fake))
            {
                r.T.Start(8084);
                Assert.That(r.WaitFor("lost", 20), Is.True, "lost when the process dies after ready");
                r.WaitUntil(() => r.States.Count >= 3, 3);
                Assert.That(r.States.ToArray(), Is.EqualTo(new[] { "starting", "ready", "lost" }));
                Assert.That(r.T.Url, Is.EqualTo(""), "url cleared");

                // restart after failure path: same instance can start again
                Fx.Mode("ok");
                r.T.Start(8085);
                Assert.That(r.WaitFor("ready", 15), Is.True, "same tunnel can start again");
                r.T.Stop();
            }
        }

        // ---------------------------------------------------------------- retries / fallback

        [Test]
        public void RateLimit429RetriesThenReady()
        {
            var marker = Path.Combine(Fx.NewTempDir(), "fake_once.marker");
            _dirs.Add(Path.GetDirectoryName(marker));
            Fx.StateFile(marker);
            Fx.Mode("error_429_once");
            using (var r = Make(_fake))
            {
                r.T.RetryBackoffSec = 0.3f;
                r.T.Start(8090);
                Assert.That(r.WaitFor("ready", 20), Is.True, "ready after one retry (log: " + r.LogJoined + ")");
                Assert.That(r.States.Count(s => s == "starting"), Is.GreaterThanOrEqualTo(2),
                    "relaunched (states: " + string.Join(",", r.States) + ")");
                Assert.That(r.Details.Any(d => d.Contains("retry 1/")), Is.True, "retry announced");
                r.T.Stop();
            }
        }

        [Test]
        public void RetriesExhaustedFails()
        {
            Fx.Mode("error_exit");
            using (var r = Make(_fake))
            {
                r.T.MaxRetries = 1;
                r.T.RetryBackoffSec = 0.2f;
                r.T.Start(8091);
                Assert.That(r.WaitFor("failed", 20), Is.True, "failed once retries ran out");
                Assert.That(r.Details[r.Details.Count - 1], Does.Contain("1015"));
            }
        }

        [Test]
        public void QuicBlockedFallsBackToHttp2()
        {
            Fx.Mode("quic_fail");
            using (var r = Make(_fake))
            {
                r.T.ProtocolFallbackSec = 2f;
                r.T.Start(8092);
                Assert.That(r.WaitFor("ready", 20), Is.True, "ready after the http2 fallback (log: " + r.LogJoined + ")");
                Assert.That(r.LogJoined, Does.Contain("HTTP/2"));
                Assert.That(r.T.Url, Is.EqualTo("https://random-words-here.trycloudflare.com"), "second launch's URL");
                r.T.Stop();
            }
        }

        [Test]
        public void ExtraArgsProtocolSkipsFallback()
        {
            Fx.Mode("quic_fail");
            using (var r = Make(_fake))
            {
                r.T.ExtraArgs = new[] { "--protocol", "http2" };
                r.T.Start(8093);
                Assert.That(r.WaitFor("ready", 15), Is.True, "ready with forced http2, no fallback needed");
                r.T.Stop();
            }
        }

        // ---------------------------------------------------------------- lost / recover

        [Test]
        public void UnregisteredConnectionsBecomeLost()
        {
            Fx.Mode("unregister");
            using (var r = Make(_fake))
            {
                r.T.LostGraceSec = 0.5f;
                r.T.Start(8094);
                Assert.That(r.WaitFor("ready", 15), Is.True, "ready");
                Assert.That(r.WaitFor("lost", 15), Is.True, "lost after the grace period");
                Assert.That(r.T.IsRunning(), Is.True, "process still alive while lost");
                Assert.That(r.T.Url, Is.EqualTo("https://random-words-here.trycloudflare.com"), "url kept while alive");
                r.T.Stop();
            }
        }

        [Test]
        public void ReregistrationReturnsToReady()
        {
            Fx.Mode("unregister_recover");
            using (var r = Make(_fake))
            {
                r.T.LostGraceSec = 0.5f;
                r.T.Start(8095);
                Assert.That(r.WaitFor("ready", 15), Is.True, "ready");
                Assert.That(r.WaitFor("lost", 15), Is.True, "lost");
                Assert.That(r.WaitUntil(() => r.T.State == "ready" && r.States.Count(s => s == "ready") >= 2, 20), Is.True, "recovered to ready");
                r.T.Stop();
            }
        }

        // ---------------------------------------------------------------- named mode

        [Test]
        public void NamedTunnelToken()
        {
            Fx.Mode("named_ok");
            using (var r = Make(_fake))
            {
                r.T.Mode = "named";
                r.T.NamedToken = "TESTTOKEN";
                r.T.NamedHostname = "party.example.com";
                r.T.Start(8096);
                Assert.That(r.WaitFor("ready", 15), Is.True, "named ready (log: " + r.LogJoined + ")");
                Assert.That(r.T.Url, Is.EqualTo("https://party.example.com"), "stable hostname is the URL");
                r.T.Stop();
            }
        }

        [Test]
        public void NamedTunnelCredentialsFile()
        {
            var cred = Path.Combine(Fx.NewTempDir(), "creds.json");
            _dirs.Add(Path.GetDirectoryName(cred));
            File.WriteAllText(cred, "{\"AccountTag\":\"a\",\"TunnelSecret\":\"b\",\"TunnelID\":\"c\"}");
            Fx.Mode("named_local");
            using (var r = Make(_fake))
            {
                r.T.Mode = "named";
                r.T.NamedName = "mytunnel";
                r.T.NamedCredentialsFile = cred;
                r.T.NamedHostname = "party.example.com";
                r.T.Start(8097);
                Assert.That(r.WaitFor("ready", 15), Is.True, "named ready (log: " + r.LogJoined + ")");
                var ncfg = File.ReadAllText(Path.Combine(r.Dir, PmcTunnel.NamedConfigName));
                Assert.That(ncfg, Does.Contain("tunnel: mytunnel"));
                Assert.That(ncfg, Does.Contain("hostname: party.example.com"));
                Assert.That(ncfg, Does.Contain("service: http://127.0.0.1:8097"));
                r.T.Stop();
            }
        }

        [Test]
        public void NamedModeValidation()
        {
            Fx.Mode("ok");
            using (var r = Make(_fake))
            {
                r.T.Mode = "named";
                var code = r.T.Start(8098);
                r.T.Pump();
                Assert.That(code, Is.EqualTo(PmcTunnel.ErrInvalidConfig));
                Assert.That(r.States.ToArray(), Is.EqualTo(new[] { "failed" }), "missing named settings -> failed fast");
                Assert.That(r.Details[0], Does.Contain("NamedHostname"));
            }
        }

        // ---------------------------------------------------------------- config / pid file

        [Test]
        public void DefaultConfigIsolation()
        {
            var cdir = Fx.NewTempDir();
            _dirs.Add(cdir);
            File.WriteAllText(Path.Combine(cdir, "config.yml"), "tunnel: bogus\n");
            Assert.That(PmcTunnel.FindDefaultConfig(new[] { cdir }),
                Is.EqualTo(Path.Combine(cdir, "config.yml")), "config detected");
            Fx.Mode("needs_config");
            using (var r = Make(_fake))
            {
                r.T.ConfigDirs = new[] { cdir };
                r.T.Start(8099);
                Assert.That(r.WaitFor("ready", 15), Is.True, "ready with an isolated --config (log: " + r.LogJoined + ")");
                Assert.That(r.LogJoined, Does.Contain("default cloudflared config exists"));
                r.T.Stop();
            }
        }

        [Test]
        public void PidFileWrittenAndRemoved()
        {
            Fx.Mode("ok");
            var dir = Fx.NewTempDir();
            _dirs.Add(dir);
            using (var r = Make(_fake))
            {
                r.T.TempDir = dir;
                var pidFile = Path.Combine(dir, PmcTunnel.PidFileName);
                r.T.Start(8100);
                Assert.That(r.WaitFor("ready", 15), Is.True, "ready");
                Assert.That(File.Exists(pidFile), Is.True, "pid file written");
                var rec = JObject.Parse(File.ReadAllText(pidFile));
                Assert.That((int)rec["pid"], Is.EqualTo(r.T.Pid), "records the child pid");
                r.T.Stop();
                r.WaitUntil(() => !File.Exists(pidFile), 3);
                Assert.That(File.Exists(pidFile), Is.False, "pid file removed on stop");
            }
        }

        [Test]
        public void StalePidReaped()
        {
            var dir = Fx.NewTempDir();
            _dirs.Add(dir);
            Fx.Mode("ok");
            var orphan = Fx.SpawnFake("tunnel", "--no-autoupdate", "--url", "http://127.0.0.1:9");
            var oldPid = orphan.Id;
            Assert.That(oldPid, Is.GreaterThan(0), "leftover fake spawned");
            var pidFile = Path.Combine(dir, PmcTunnel.PidFileName);
            File.WriteAllText(pidFile, new JObject
            {
                ["pid"] = oldPid,
                ["exe"] = _fake,
                ["port"] = 9,
            }.ToString());

            using (var r = Make(_fake))
            {
                r.T.TempDir = dir;
                r.T.Start(8101);
                Assert.That(r.WaitFor("ready", 15), Is.True, "new tunnel ready (log: " + r.LogJoined + ")");
                Assert.That(r.WaitUntil(() => !Fx.ProcAlive(oldPid), 8), Is.True, "leftover cloudflared killed");
                Assert.That(r.LogJoined, Does.Contain("leftover cloudflared"));
            }

            // A pid file naming a live non-cloudflared process (ourselves) must be left alone.
            File.WriteAllText(pidFile, new JObject
            {
                ["pid"] = Process.GetCurrentProcess().Id,
                ["exe"] = _fake,
            }.ToString());
            using (var r2 = Make(_fake))
            {
                r2.T.TempDir = dir;
                r2.T.Start(8102);
                Assert.That(r2.WaitFor("ready", 15), Is.True, "ready");
                Assert.That(Fx.ProcAlive(Process.GetCurrentProcess().Id), Is.True, "own process untouched");
                Assert.That(r2.LogJoined, Does.Contain("left alone"));
                r2.T.Stop();
            }
        }

        [Test]
        public void DisposeKillsProcess()
        {
            Fx.Mode("ok");
            var r = Make(_fake);
            r.T.Start(8086);
            Assert.That(r.WaitFor("ready", 15), Is.True, "ready before dispose");
            var pid = r.T.Pid;
            r.T.Dispose();
            Assert.That(WaitUntilNoPump(() => !Fx.ProcAlive(pid), 5), Is.True, "process killed on dispose");
        }

        static bool WaitUntilNoPump(Func<bool> cond, double sec)
        {
            var until = DateTime.UtcNow.AddSeconds(sec);
            while (DateTime.UtcNow < until)
            {
                if (cond())
                    return true;
                System.Threading.Thread.Sleep(15);
            }
            return cond();
        }
    }
}
