using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Splatter.Pmc;

namespace Splatter.Pmc.Core.Tests
{
    /// <summary>Locates tests/fixtures/tunnel relative to the test assembly, sets the fake's env vars,
    /// and records StateChanged emissions while Pump-driving a PmcTunnel.</summary>
    static class Fx
    {
        internal static string FixtureDir()
        {
            var d = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            while (d != null)
            {
                var cand = Path.Combine(d.FullName, "tests", "fixtures", "tunnel");
                if (Directory.Exists(cand))
                    return cand;
                d = d.Parent;
            }
            // last resort: relative to the CWD (dotnet test usually runs in tests/dotnet)
            var rel = Path.GetFullPath(Path.Combine("..", "fixtures", "tunnel"));
            if (Directory.Exists(rel))
                return rel;
            Assert.Fail("could not locate tests/fixtures/tunnel from " + TestContext.CurrentContext.TestDirectory);
            return null;
        }

        internal static bool IsWindows
        {
            get { return System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows); }
        }

        internal static string Fake()
        {
            var f = Path.Combine(FixtureDir(), IsWindows ? "fake_cloudflared.cmd" : "fake_cloudflared.sh");
            Assert.That(File.Exists(f), Is.True, "fixture missing: " + f);
            if (!IsWindows)
            {
                // The exec bit may not survive a zip/tar checkout — make sure.
                try
                {
                    var psi = new ProcessStartInfo("chmod", "+x \"" + f + "\"") { UseShellExecute = false };
                    using (var p = Process.Start(psi))
                        p.WaitForExit(10000);
                }
                catch { }
            }
            return f;
        }

        internal static void Mode(string mode)
        {
            Environment.SetEnvironmentVariable("PMC_FAKE_CLOUDFLARED_MODE", mode);
        }

        internal static void StateFile(string path)
        {
            Environment.SetEnvironmentVariable("PMC_FAKE_STATE_FILE", path);
        }

        internal static void ClearEnv()
        {
            Environment.SetEnvironmentVariable("PMC_FAKE_CLOUDFLARED_MODE", null);
            Environment.SetEnvironmentVariable("PMC_FAKE_STATE_FILE", null);
        }

        /// <summary>Spawns the fixture outside PmcTunnel (used to plant a stale pid file).</summary>
        internal static Process SpawnFake(params string[] args)
        {
            var fake = Fake();
            ProcessStartInfo psi;
            if (IsWindows)
            {
                psi = new ProcessStartInfo("cmd.exe", "/d /s /c \"\"" + fake + "\" " + string.Join(" ", args) + "\"");
            }
            else
            {
                psi = new ProcessStartInfo("sh");
                psi.ArgumentList.Add(fake);
                foreach (var a in args)
                    psi.ArgumentList.Add(a);
            }
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            var p = Process.Start(psi);
            Assert.That(p, Is.Not.Null, "could not spawn fixture");
            return p;
        }

        internal static bool ProcAlive(int pid)
        {
            try
            {
                using (var p = Process.GetProcessById(pid))
                    return !p.HasExited;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>A throwaway temp dir per test — isolates the pid file, generated configs and bin/.</summary>
        internal static string NewTempDir()
        {
            var d = Path.Combine(Path.GetTempPath(), "pmc-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(d);
            return d;
        }

        internal static void RemoveDir(string d)
        {
            try
            {
                if (Directory.Exists(d))
                    Directory.Delete(d, true);
            }
            catch { }
        }
    }

    /// <summary>PmcTunnel plus recorded state transitions; WaitFor* helpers pump the tunnel.</summary>
    sealed class Rec : IDisposable
    {
        public readonly PmcTunnel T;
        public readonly List<string> States = new List<string>();
        public readonly List<string> Details = new List<string>();
        public readonly string Dir;

        public Rec(string bin, string dir = null)
        {
            Dir = dir ?? Fx.NewTempDir();
            T = new PmcTunnel
            {
                BinaryPath = bin,
                AllowDownload = false,
                VerifyDns = false,
                TempDir = Dir,
            };
            T.StateChanged += (s, d) =>
            {
                States.Add(s);
                Details.Add(d);
            };
        }

        public bool WaitUntil(Func<bool> cond, double sec)
        {
            var until = DateTime.UtcNow.AddSeconds(sec);
            while (DateTime.UtcNow < until)
            {
                T.Pump();
                if (cond())
                    return true;
                Thread.Sleep(15);
            }
            T.Pump();
            return cond();
        }

        public bool WaitFor(string state, double sec = 15)
        {
            return WaitUntil(() => T.State == state, sec);
        }

        public string LogJoined
        {
            get { return string.Join(" | ", T.LogLines); }
        }

        public void Dispose()
        {
            T.Dispose();
            Fx.RemoveDir(Dir);
        }
    }

    /// <summary>Minimal HTTP responder: answers 200 + a JSON dns-json body (or plain text) to every
    /// request. Raw TCP so no URL ACLs or admin rights are needed.</summary>
    sealed class TinyHttp : IDisposable
    {
        readonly TcpListener _l;
        readonly CancellationTokenSource _cts = new CancellationTokenSource();
        readonly string _body;
        readonly string _contentType;

        public int Port { get; private set; }

        public TinyHttp(string body, string contentType)
        {
            _body = body;
            _contentType = contentType;
            _l = new TcpListener(IPAddress.Loopback, 0);
            _l.Start();
            Port = ((IPEndPoint)_l.LocalEndpoint).Port;
            Task.Run(Loop);
        }

        async Task Loop()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient c;
                try
                {
                    c = await _l.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                }
                catch { break; }
                var _ = Task.Run(() => Serve(c));
            }
        }

        async Task Serve(TcpClient c)
        {
            try
            {
                using (c)
                {
                    var s = c.GetStream();
                    var ms = new MemoryStream();
                    var buf = new byte[8192];
                    var sw = Stopwatch.StartNew();
                    while (sw.ElapsedMilliseconds < 5000)
                    {
                        if (s.DataAvailable)
                        {
                            var n = await s.ReadAsync(buf, 0, buf.Length).ConfigureAwait(false);
                            if (n <= 0)
                                break;
                            ms.Write(buf, 0, n);
                            if (Encoding.ASCII.GetString(ms.ToArray()).Contains("\r\n\r\n"))
                                break;
                        }
                        else
                        {
                            await Task.Delay(5).ConfigureAwait(false);
                        }
                    }
                    var body = Encoding.ASCII.GetBytes(_body);
                    var head = "HTTP/1.1 200 OK\r\nContent-Type: " + _contentType + "\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n";
                    var hb = Encoding.ASCII.GetBytes(head);
                    await s.WriteAsync(hb, 0, hb.Length).ConfigureAwait(false);
                    await s.WriteAsync(body, 0, body.Length).ConfigureAwait(false);
                }
            }
            catch { }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _l.Stop(); }
            catch { }
            _cts.Dispose();
        }
    }
}
