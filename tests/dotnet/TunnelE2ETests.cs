using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Splatter.Pmc;

namespace Splatter.Pmc.Core.Tests
{
    /// <summary>
    /// Real end-to-end Cloudflare Quick Tunnel test. Opt-in: set PMC_TUNNEL_E2E=1.
    /// Downloads cloudflared if needed, opens a quick tunnel to a loopback responder, then fetches
    /// https://&lt;random&gt;.trycloudflare.com/pmc/healthz over the public internet.
    /// Port of the Godot test_tunnel_e2e.gd (the Unity host-side wss flow is covered by the host suite).
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class TunnelE2ETests
    {
        [Test]
        public void RealQuickTunnel()
        {
            if (Environment.GetEnvironmentVariable("PMC_TUNNEL_E2E") != "1")
                Assert.Ignore("set PMC_TUNNEL_E2E=1 to run the real Cloudflare quick tunnel test");

            var dir = Path.Combine(Path.GetTempPath(), "pmc-e2e");
            var bin = PmcTunnel.ResolveBinary(Environment.GetEnvironmentVariable("PMC_CLOUDFLARED") ?? "");
            if (bin == "")
            {
                using (var dl = new PmcTunnel { AllowDownload = true, TempDir = dir })
                {
                    var done = new TaskCompletionSource<bool>();
                    dl.DownloadFinished += (ok, info) => done.TrySetResult(ok);
                    dl.Download();
                    var until = DateTime.UtcNow.AddSeconds(240);
                    while (!done.Task.IsCompleted && DateTime.UtcNow < until)
                    {
                        dl.Pump();
                        Thread.Sleep(20);
                    }
                    Assert.That(done.Task.IsCompleted && done.Task.Result, Is.True, "download finished: " + dl.LastError);
                }
                bin = PmcTunnel.ResolveBinary("", dir);
                Assert.That(bin, Is.Not.EqualTo(""), "cloudflared resolved after download");
            }

            using (var responder = new TinyHttp("ok", "text/plain"))
            using (var t = new PmcTunnel
            {
                BinaryPath = bin,
                AllowDownload = false,
                VerifyDns = true, // real DNS-over-HTTPS against cloudflare-dns.com
                ReadyTimeoutSec = 90f,
                TempDir = dir,
            })
            {
                var states = new System.Collections.Generic.List<string>();
                t.StateChanged += (s, d) => states.Add(s + ":" + d);
                Assert.That(t.Start(responder.Port), Is.EqualTo(PmcTunnel.Ok));
                var ready = WaitFor(() => t.State == "ready" || t.State == "failed", () => t.Pump(), 90);
                Assert.That(ready, Is.True, "tunnel reached ready/failed (" + string.Join(", ", states) + ")");
                Assert.That(t.State, Is.EqualTo("ready"), "tunnel ready");
                Assert.That(t.Url, Does.Match("^https://[a-z0-9-]+\\.trycloudflare\\.com$"));

                var fetched = false;
                var f0 = DateTime.UtcNow;
                using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) })
                {
                    while (!fetched && DateTime.UtcNow - f0 < TimeSpan.FromSeconds(90))
                    {
                        try
                        {
                            var body = http.GetStringAsync(t.Url + "/pmc/healthz").GetAwaiter().GetResult();
                            if (body.Contains("ok"))
                                fetched = true;
                        }
                        catch { }
                        if (!fetched)
                        {
                            t.Pump();
                            Thread.Sleep(2000);
                        }
                    }
                }
                Assert.That(fetched, Is.True, "GET /pmc/healthz through the tunnel");
                t.Stop();
                WaitFor(() => t.State == "stopped", () => t.Pump(), 5);
            }
        }

        static bool WaitFor(Func<bool> cond, Action pump, double sec)
        {
            var until = DateTime.UtcNow.AddSeconds(sec);
            while (DateTime.UtcNow < until)
            {
                pump();
                if (cond())
                    return true;
                Thread.Sleep(15);
            }
            return cond();
        }
    }
}
