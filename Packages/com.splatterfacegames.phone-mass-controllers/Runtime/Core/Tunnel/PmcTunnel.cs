using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace Splatter.Pmc
{
    /// <summary>
    /// Outside-LAN access through a Cloudflare tunnel — quick or named. Engine-free port of the
    /// Godot PMCTunnel: <c>System.Diagnostics.Process</c> + reader threads feed a queue that
    /// <see cref="Pump"/> drains; every state transition happens inside <see cref="Pump"/> (or on
    /// the thread calling <see cref="Start"/>/<see cref="Stop"/> — the host Poll thread).
    ///
    /// Quick mode (<see cref="Mode"/> <c>"quick"</c>) runs
    /// <c>cloudflared tunnel --no-autoupdate --url http://127.0.0.1:&lt;port&gt;</c>, reads stderr
    /// for the <c>https://&lt;random&gt;.trycloudflare.com</c> URL and reports progress through
    /// <see cref="StateChanged"/>. Quick tunnels need no Cloudflare account but the URL is
    /// ephemeral, best-effort and rate-limited.
    ///
    /// Named mode (<see cref="Mode"/> <c>"named"</c>) runs a tunnel from your own Cloudflare
    /// account on a stable hostname: either the dashboard token (<see cref="NamedToken"/> +
    /// <see cref="NamedHostname"/>) or a locally created tunnel (<see cref="NamedName"/> +
    /// <see cref="NamedCredentialsFile"/> + <see cref="NamedHostname"/>).
    /// </summary>
    /// <remarks>
    /// Progress states: <c>"downloading"</c> (detail = progress text), <c>"starting"</c> (detail =
    /// binary path or retry note), <c>"ready"</c> (detail = public URL), <c>"lost"</c> (detail =
    /// reason — the tunnel was up and dropped; it can return to <c>"ready"</c> while the process
    /// lives), <c>"failed"</c> (detail = reason), <c>"stopped"</c>. <c>"idle"</c> is the initial
    /// state before the first <see cref="Start"/>.
    /// </remarks>
    public sealed partial class PmcTunnel : IDisposable
    {
        /// <summary>Directory the downloader installs cloudflared into, relative to <see cref="TempDir"/>.</summary>
        public const string InstallDirName = "bin";
        /// <summary>GitHub "latest release" download base for the official cloudflared binaries.</summary>
        public const string ReleaseBase = "https://github.com/cloudflare/cloudflared/releases/latest/download/";
        /// <summary>GitHub API endpoint describing the latest release (asset URLs and SHA-256 digests).</summary>
        public const string ReleaseApi = "https://api.github.com/repos/cloudflare/cloudflared/releases/latest";
        /// <summary>Pid record file name under <see cref="TempDir"/> — lets a later Start reap an orphan.</summary>
        public const string PidFileName = "cloudflared.pid";
        /// <summary>Written on demand; an (almost) empty config isolates us from a default ~/.cloudflared/config.yml.</summary>
        public const string EmptyConfigName = "empty-cloudflared.yml";
        /// <summary>Generated config for named mode with name + credentials file.</summary>
        public const string NamedConfigName = "named-tunnel.yml";
        /// <summary>Prefix for this class's own diagnostic lines inside <see cref="LogLines"/>.</summary>
        public const string LogPrefix = "PmcTunnel:";

        /// <summary><see cref="Start"/> accepted the request.</summary>
        public const int Ok = 0;
        /// <summary><see cref="Start"/> failed validation (bad mode or missing named-* settings).</summary>
        public const int ErrInvalidConfig = 1;
        /// <summary><see cref="Start"/> found no cloudflared and <see cref="AllowDownload"/> is off.</summary>
        public const int ErrNoBinary = 2;
        /// <summary><see cref="Start"/> on a platform that can't spawn a child process.</summary>
        public const int ErrUnsupported = 3;

        const int MaxLogLines = 200;

        // --- public state (contract) -------------------------------------------

        /// <summary>Current state: idle | downloading | starting | ready | lost | failed | stopped.</summary>
        public string State { get; private set; } = "idle";
        /// <summary>Public tunnel URL once "ready", else "". In named mode it is
        /// <c>https://&lt;NamedHostname&gt;</c> and is known as soon as the process launches.</summary>
        public string Url { get; private set; } = "";
        /// <summary>Most recent failure reason or cloudflared ERR line, "" when nothing went wrong.</summary>
        public string LastError { get; private set; } = "";
        /// <summary>Recent cloudflared log lines (newest last, capped at 200), useful for diagnostics.</summary>
        public List<string> LogLines { get; } = new List<string>();
        /// <summary>Raised for every state transition — always on the thread that calls <see cref="Pump"/>.</summary>
        public event Action<string, string> StateChanged;
        /// <summary>Emitted when a download started by <see cref="Download"/> or <see cref="Start"/> finishes.
        /// The string is the installed binary path on success, else the reason.</summary>
        public event Action<bool, string> DownloadFinished;

        // --- options (set before Start) -----------------------------------------

        /// <summary><c>"quick"</c> (account-less, random trycloudflare URL) or <c>"named"</c>
        /// (your Cloudflare account, stable hostname).</summary>
        public string Mode = "quick";
        /// <summary>Named mode: token from the dashboard's "run with token" flow.</summary>
        public string NamedToken = "";
        /// <summary>Named mode: the public hostname routed to the tunnel (e.g. party.example.com). Required.</summary>
        public string NamedHostname = "";
        /// <summary>Named mode without a token: tunnel name or UUID from <c>cloudflared tunnel create</c>.</summary>
        public string NamedName = "";
        /// <summary>Named mode without a token: credentials JSON written by <c>cloudflared tunnel create</c>.</summary>
        public string NamedCredentialsFile = "";
        /// <summary>Explicit cloudflared executable. Empty = env <c>PMC_CLOUDFLARED</c>, then PATH, then the
        /// downloaded copy under <see cref="TempDir"/>.</summary>
        public string BinaryPath = "";
        /// <summary>Allow downloading cloudflared when it can't be found. Editor tooling should set this;
        /// shipping games default off.</summary>
        public bool AllowDownload = false;
        /// <summary>Before reporting "ready", wait until the new hostname resolves in public DNS (checked over
        /// DNS-over-HTTPS so the system resolver doesn't cache an early NXDOMAIN).</summary>
        public bool VerifyDns = true;
        /// <summary>Seconds to wait for the URL and the first registered edge connection before failing.</summary>
        public float ReadyTimeoutSec = 60f;
        /// <summary>Extra arguments appended to the cloudflared command line (e.g. --protocol http2).
        /// In named mode they land after <c>run</c>.</summary>
        public string[] ExtraArgs = new string[0];
        /// <summary>Retries when tunnel creation or registration fails before "ready" (e.g. HTTP 429 /
        /// error 1015 rate limiting), with exponential backoff.</summary>
        public int MaxRetries = 2;
        /// <summary>Delay before the first retry, in seconds; doubles each retry.</summary>
        public float RetryBackoffSec = 4f;
        /// <summary>Seconds without a registered edge connection (after the URL exists) that count as
        /// "QUIC blocked" — the process relaunches once with --protocol http2.</summary>
        public float ProtocolFallbackSec = 20f;
        /// <summary>All edge connections may stay unregistered this long while "ready" before the state
        /// becomes "lost". A re-registration returns to "ready".</summary>
        public float LostGraceSec = 10f;
        /// <summary>Re-verify the downloaded binary against the latest release once it's older than this many
        /// days (0 = never). Needs <see cref="AllowDownload"/> to refresh; otherwise the cached copy is
        /// re-checked in place.</summary>
        public int BinaryMaxAgeDays = 30;
        /// <summary>Minimum accepted cloudflared version ("YYYY.M.D"). Older resolved binaries are rejected.</summary>
        public string MinimumVersion = "2022.6.2";

        // --- options not pinned by the contract (ported from the Godot vars) ------

        /// <summary>Verify the download against the SHA-256 digest GitHub publishes for the release asset. When
        /// the digest can't be fetched the download fails instead of running an unverified binary.</summary>
        public bool VerifyChecksum = true;
        /// <summary>Verify the code signature of downloaded/managed binaries: Authenticode (must be valid and
        /// signed by Cloudflare, Inc. when a signature is present) on Windows, <c>codesign --verify</c> on
        /// macOS. Unsigned binaries fall back to the SHA-256 check.</summary>
        public bool VerifySignature = true;
        /// <summary>Kill a leftover cloudflared recorded in the pid file before starting (crash recovery).</summary>
        public bool ReapOrphan = true;
        /// <summary>When QUIC (outbound UDP 7844) looks blocked, relaunch once with --protocol http2 (plain
        /// TCP 443). Skipped when <see cref="ExtraArgs"/> already sets a protocol.</summary>
        public bool ProtocolFallback = true;
        /// <summary>Longest wait for the DNS check; after this the tunnel is reported ready anyway.</summary>
        public float DnsTimeoutSec = 30f;
        /// <summary>DNS-over-HTTPS JSON endpoint used by <see cref="VerifyDns"/>.</summary>
        public string DnsOverHttpsUrl = "https://cloudflare-dns.com/dns-query";
        /// <summary>Directories checked for a default cloudflared config.yml/.yaml (which breaks quick
        /// tunnels). Empty = platform defaults. When one is found the tunnel launches with an isolated
        /// <c>--config</c> instead of failing.</summary>
        public string[] ConfigDirs = new string[0];
        /// <summary>Writable directory for the pid file, generated configs and the downloaded binary.
        /// Defaults to <c>&lt;system temp&gt;/pmc</c>; tests point it at a throwaway dir.</summary>
        public string TempDir = DefaultTempDir;
        /// <summary>Test seam: replaces the DNS-over-HTTPS probe used by <see cref="VerifyDns"/>. Arguments are
        /// the hostname and a cancellation token; return true once the name resolves publicly. Runs on a
        /// worker thread — do not touch engine APIs inside.</summary>
        public Func<string, CancellationToken, System.Threading.Tasks.Task<bool>> DnsProbe;

        /// <summary>Default writable root: <c>Path.GetTempPath()/pmc</c>.</summary>
        public static string DefaultTempDir
        {
            get { return Path.Combine(Path.GetTempPath(), "pmc"); }
        }

        // --- internals ------------------------------------------------------------

        static readonly Stopwatch s_clock = Stopwatch.StartNew();
        static long NowMs { get { return s_clock.ElapsedMilliseconds; } }

        int _port;
        int _pid = -1;
        /// <summary>Child process id, or -1. Exposed for tests/diagnostics.</summary>
        public int Pid { get { return _pid; } }
        /// <summary>Local port passed to the last <see cref="Start"/>.</summary>
        public int Port { get { return _port; } }

        Process _proc;
        string _bin = "";
        Thread _readerErr, _readerOut, _watcher;
        readonly Dictionary<string, bool> _conns = new Dictionary<string, bool>(); // connIndex -> live
        long _connsLostMs;
        bool _registered;
        bool _quicFailed;        // QUIC-specific error seen in this launch's log
        bool _useHttp2;          // current launches inject --protocol http2
        bool _fellBackHttp2;     // http2 fallback already used this Start()
        int _retries;            // creation-failure retries used this Start()
        long _retryAtMs;         // pending relaunch time (0 = none)
        long _startedMs;
        long _deadlineMs;
        string _lastErrLine = "";
        long _dnsStartedMs;      // 0 = not waiting; -1 = answered/timed out
        CancellationTokenSource _dnsCts;
        int _gen;                // bumped on every launch/kill; stale queued events are dropped
        int _pumpThreadId;       // thread that calls Pump; SetState emits inline there

        // ordered queue of work for Pump(); single queue keeps event ordering deterministic
        readonly Queue<KeyValuePair<int, Action>> _events = new Queue<KeyValuePair<int, Action>>();
        readonly object _eventsLock = new object();

        static readonly HashSet<int> s_livePids = new HashSet<int>(); // pids owned by live PmcTunnels here
        static readonly object s_livePidsLock = new object();
        static readonly HashSet<string> s_checkedBins = new HashSet<string>();
        static readonly object s_checkedBinsLock = new object();

        // --- lifecycle -------------------------------------------------------------

        /// <summary>
        /// Starts a tunnel to <c>http://127.0.0.1:&lt;localPort&gt;</c>. Resolves (or downloads)
        /// cloudflared first. Restarts if already running. Returns <see cref="Ok"/>, or
        /// <see cref="ErrInvalidConfig"/>/<see cref="ErrNoBinary"/>/<see cref="ErrUnsupported"/> when it
        /// fails before launching — in that case <see cref="State"/> is "failed" and
        /// <see cref="LastError"/> has the reason.
        /// </summary>
        public int Start(int localPort)
        {
            if (State != "idle" && State != "stopped" && State != "failed")
                KillInternal(false);
            _port = localPort;
            Url = "";
            _retries = 0;
            _retryAtMs = 0;
            _deadlineMs = 0;
            _quicFailed = false;
            _useHttp2 = false;
            _fellBackHttp2 = false;
            _conns.Clear();
            _connsLostMs = 0;
            _registered = false;
            _lastErrLine = "";
            LastError = "";
            var why = UnsupportedReason();
            var code = ErrUnsupported;
            if (why == "")
            {
                why = ValidateMode();
                code = ErrInvalidConfig;
            }
            if (why != "")
            {
                Fail(why);
                return code;
            }
            if (ReapOrphan)
                ReapStale();
            var bin = ResolveBinary(BinaryPath, TempDir);
            if (bin != "")
            {
                if (IsStale(bin) && AllowDownload)
                {
                    _downloadThenStart = true;
                    BeginDownload();
                    return Ok;
                }
                var bwhy = CheckBinary(bin);
                if (bwhy != "")
                {
                    Fail(bwhy);
                    return ErrInvalidConfig;
                }
                Launch(bin);
                return Ok;
            }
            if (!AllowDownload)
            {
                Fail("cloudflared not found. Set BinaryPath or PMC_CLOUDFLARED, put it on PATH, or allow downloading it");
                return ErrNoBinary;
            }
            _downloadThenStart = true;
            BeginDownload();
            return Ok;
        }

        /// <summary>Kills cloudflared (or cancels a download) and emits "stopped" on the next Pump.</summary>
        public void Stop()
        {
            var was = State;
            KillInternal(false);
            if (was != "stopped")
                SetState("stopped", "");
        }

        /// <summary>Is the tunnel process running (starting, ready or lost-but-alive)?</summary>
        public bool IsRunning()
        {
            return _proc != null && !_proc.HasExited;
        }

        // --- Test/host seams (probed by PmcTunnelSeam + tests/dotnet/HostMirror) -------------

        /// <summary>The local port this tunnel forwards to; mirrors <see cref="Port"/> but settable
        /// so tests can fake adoption/retarget without spawning a process.</summary>
        internal int LocalPort { get { return _port; } set { _port = value; } }

        /// <summary>Test override: when true, <see cref="PmcTunnelSeam.IsProcessAlive"/> reports the
        /// child as alive regardless of process state. Never set by production code.</summary>
        internal bool _isProcessAlive;

        /// <summary>Marked when the tunnel must outlive its host object (host detaches it into a
        /// static registry on Stop instead of killing it). <see cref="Dispose"/> honors it.</summary>
        internal bool Detached;

        /// <summary>Download progress 0.0–1.0, or -1 when no download is running or its size is unknown.</summary>
        public float GetDownloadProgress()
        {
            if (_dlCts == null || _dlTotal <= 0)
                return -1f;
            var p = _dlReceived / (float)_dlTotal;
            return p < 0f ? 0f : (p > 1f ? 1f : p);
        }

        /// <summary>Silent teardown — kills the child and cancels pending work without emitting
        /// "stopped" (the Godot PREDELETE path did the same; subscribers may already be gone).</summary>
        public void Dispose()
        {
            if (!Detached) KillInternal(true);
        }

        // --- Pump: the only place queued events are applied ------------------------

        /// <summary>
        /// Drain the process-reader/DNS/download queues and drive time-based transitions (retry
        /// backoff, ready timeout, http2 fallback trigger, lost grace). The host calls this from
        /// Poll(); <see cref="StateChanged"/> subscribers observe it on this thread.
        /// </summary>
        public void Pump()
        {
            _pumpThreadId = Environment.CurrentManagedThreadId;
            DrainEvents();
            var now = NowMs;
            if (_pid <= 0)
            {
                if (_retryAtMs > 0)
                {
                    if (now > _deadlineMs)
                    {
                        _retryAtMs = 0;
                        Fail(string.Format("timed out after {0:0} s waiting for the tunnel{1}", ReadyTimeoutSec, ErrSuffix()));
                    }
                    else if (now >= _retryAtMs)
                    {
                        _retryAtMs = 0;
                        Launch(_bin);
                    }
                }
                return;
            }
            if (State == "starting" && WantHttp2Fallback(now))
                FallBackHttp2();
            else if (State == "ready" && _connsLostMs > 0 && now - _connsLostMs >= (long)(LostGraceSec * 1000.0))
            {
                _connsLostMs = 0;
                SetState("lost", "all tunnel connections unregistered");
            }
            if (State == "starting" && _dnsStartedMs > 0)
            {
                // The DNS worker drives this phase; it posts back resolution or its own timeout.
            }
            else if (State == "starting" && _deadlineMs > 0 && now > _deadlineMs)
            {
                var detail = string.Format("timed out after {0:0} s waiting for the tunnel", ReadyTimeoutSec);
                detail += Url == "" ? " URL" : " connection";
                if (_lastErrLine != "")
                    detail += ": " + FriendlyError(_lastErrLine);
                KillInternal(false);
                Fail(detail);
            }
        }

        void DrainEvents()
        {
            while (true)
            {
                KeyValuePair<int, Action> item;
                lock (_eventsLock)
                {
                    if (_events.Count == 0)
                        return;
                    item = _events.Dequeue();
                }
                if (item.Key == _gen)
                    item.Value();
            }
        }

        void Post(int gen, Action a)
        {
            lock (_eventsLock)
            {
                _events.Enqueue(new KeyValuePair<int, Action>(gen, a));
            }
        }

        // --- launch ------------------------------------------------------------------

        string ValidateMode()
        {
            if (Mode == "quick")
                return "";
            if (Mode != "named")
                return "unknown tunnel mode '" + Mode + "' (expected quick or named)";
            if (NamedHostname == null || NamedHostname.Trim() == "")
                return "named mode needs NamedHostname (the public hostname routed to your tunnel)";
            if (NamedToken == "" && (NamedName == "" || NamedCredentialsFile == ""))
                return "named mode needs NamedToken, or NamedName + NamedCredentialsFile";
            return "";
        }

        string NamedUrl()
        {
            if (Mode != "named")
                return "";
            var h = (NamedHostname ?? "").Trim();
            if (h == "")
                return "";
            var i = h.IndexOf("://", StringComparison.Ordinal);
            if (i >= 0)
                h = h.Substring(i + 3);
            return "https://" + h.TrimEnd('/');
        }

        void Launch(string bin)
        {
            _bin = bin;
            _registered = false;
            _conns.Clear();
            _connsLostMs = 0;
            _quicFailed = false;
            _dnsStartedMs = 0;
            StopDns();
            _lastErrLine = "";
            Url = NamedUrl();
            // Keep our own diagnostic lines (orphan reap, config isolation, earlier fallback note)
            // across the per-launch clear; only cloudflared's log is reset.
            LogLines.RemoveAll(l => !l.StartsWith(LogPrefix, StringComparison.Ordinal));
            var args = BuildArgs();
            if (args == null)
                return; // Fail already called
            var gen = ++_gen;
            Process proc;
            try
            {
                proc = Spawn(bin, args);
            }
            catch (Exception e)
            {
                Fail("failed to launch " + bin + ": " + e.Message);
                return;
            }
            _proc = proc;
            _pid = proc.Id;
            _startedMs = NowMs;
            if (_deadlineMs == 0)
                _deadlineMs = _startedMs + (long)(ReadyTimeoutSec * 1000.0);
            lock (s_livePidsLock)
                s_livePids.Add(_pid);
            WritePidfile();
            var err = proc.StandardError;
            var outp = proc.StandardOutput;
            _readerErr = StartReader(err, gen);
            _readerOut = StartReader(outp, gen);
            var re = _readerErr;
            var ro = _readerOut;
            _watcher = new Thread(() => WatchProc(proc, re, ro, gen)) { IsBackground = true, Name = "PmcTunnel-exit" };
            _watcher.Start();
            SetState("starting", bin);
        }

        /// <summary>Builds the cloudflared command line. On a config problem it calls Fail and returns null.</summary>
        List<string> BuildArgs()
        {
            var args = new List<string> { "tunnel", "--no-autoupdate" };
            string cfg = "";
            if (Mode == "named" && NamedToken == "")
            {
                cfg = WriteNamedConfig();
                if (cfg == "")
                {
                    Fail("could not write " + Path.Combine(TempDir, NamedConfigName));
                    return null;
                }
            }
            else if (Mode == "quick" || NamedToken != "")
            {
                // Any default config.yml breaks quick tunnels (and can confuse token mode): isolate.
                var found = FindDefaultConfig(ConfigDirs);
                if (found != "")
                    Log(LogPrefix + " a default cloudflared config exists at " + found + "; starting with an isolated config");
                cfg = EmptyConfig();
                if (cfg == "" && found != "")
                {
                    Fail("cloudflared config " + found + " breaks " + Mode + " tunnels, and an isolated config could not be written");
                    return null;
                }
            }
            if (cfg != "")
            {
                args.Add("--config");
                args.Add(cfg);
            }
            if (_useHttp2)
            {
                args.Add("--protocol");
                args.Add("http2");
            }
            if (Mode == "named")
            {
                if (NamedToken != "")
                {
                    args.Add("run");
                    args.Add("--token");
                    args.Add(NamedToken);
                }
                else
                {
                    args.Add("run");
                    args.Add(NamedName);
                }
            }
            else
            {
                args.Add("--url");
                args.Add("http://127.0.0.1:" + _port);
            }
            if (ExtraArgs != null)
                args.AddRange(ExtraArgs);
            return args;
        }

        /// <summary>A nearly-empty config file, so a default ~/.cloudflared/config.yml can't leak in. "" on write failure.</summary>
        string EmptyConfig()
        {
            var p = Path.Combine(TempDir, EmptyConfigName);
            if (File.Exists(p))
                return p;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(p));
                File.WriteAllText(p, "no-autoupdate: true\n");
            }
            catch
            {
                return "";
            }
            return p;
        }

        /// <summary>Generated config for named mode with a credentials file. "" on write failure.</summary>
        string WriteNamedConfig()
        {
            var p = Path.Combine(TempDir, NamedConfigName);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(p));
                var cred = Path.GetFullPath(NamedCredentialsFile);
                var sb = new StringBuilder();
                sb.Append("tunnel: ").Append(NamedName.Trim()).Append('\n');
                // JSON.stringify parity: the path is written as a double-quoted, escaped scalar.
                sb.Append("credentials-file: ").Append(JToken.FromObject(cred).ToString(Newtonsoft.Json.Formatting.None)).Append('\n');
                sb.Append("ingress:\n");
                sb.Append("  - hostname: ").Append(NamedHostname.Trim()).Append('\n');
                sb.Append("    service: http://127.0.0.1:").Append(_port).Append('\n');
                sb.Append("  - service: http_status:404\n");
                File.WriteAllText(p, sb.ToString());
            }
            catch
            {
                return "";
            }
            return p;
        }

        // --- reader/watcher threads ---------------------------------------------------

        Thread StartReader(StreamReader reader, int gen)
        {
            var th = new Thread(() =>
            {
                try
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        var l = line;
                        Post(gen, () => HandleLine(l));
                    }
                }
                catch
                {
                    // pipe closed / process killed — the exit event settles the state
                }
            }) { IsBackground = true, Name = "PmcTunnel-read" };
            th.Start();
            return th;
        }

        void WatchProc(Process proc, Thread re, Thread ro, int gen)
        {
            try { proc.WaitForExit(); }
            catch { }
            // Let the readers drain to EOF first so every line lands in the queue before the exit event.
            JoinQuiet(re);
            JoinQuiet(ro);
            var code = -1;
            try { code = proc.ExitCode; }
            catch { }
            var c = code;
            Post(gen, () => OnProcessExit(c));
        }

        static void JoinQuiet(Thread t)
        {
            if (t == null)
                return;
            try { t.Join(10000); }
            catch { }
        }

        // --- log line handling (runs inside Pump) --------------------------------------

        void HandleLine(string line)
        {
            if (line == null)
                return;
            line = line.Trim();
            if (line == "")
                return;
            Log(line);
            if (line.Contains(" ERR ") || line.Contains("error=") || line.StartsWith("ERR", StringComparison.Ordinal))
            {
                _lastErrLine = line;
                LastError = line;
            }
            if (IsQuicError(line))
                _quicFailed = true;
            if (Url == "" && Mode == "quick")
            {
                var found = ParseUrl(line);
                if (found != "")
                    Url = found;
            }
            if (line.Contains("Registered tunnel connection"))
            {
                _registered = true;
                _conns[ConnIndex(line)] = true;
                _connsLostMs = 0;
                if (State == "lost")
                {
                    Log(LogPrefix + " edge connection re-registered; tunnel is back");
                    SetState("ready", Url);
                }
            }
            else if (line.Contains("Unregistered tunnel connection"))
            {
                _conns.Remove(ConnIndex(line));
                if (_conns.Count == 0 && _connsLostMs == 0 && State == "ready")
                    _connsLostMs = NowMs;
            }
            if (State == "starting" && Url != "" && _registered && _dnsStartedMs == 0)
            {
                if (VerifyDns)
                {
                    _dnsStartedMs = NowMs;
                    Log(LogPrefix + " waiting for " + Url.Substring("https://".Length) + " to resolve");
                    StartDnsWait(Url.Substring("https://".Length), _gen);
                }
                else
                {
                    SetState("ready", Url);
                }
            }
        }

        void OnProcessExit(int code)
        {
            var detail = "cloudflared exited" + (code != -1 ? " (code " + code + ")" : "");
            if (_lastErrLine != "")
                detail += ": " + FriendlyError(_lastErrLine);
            if (State == "starting" && _retries < MaxRetries)
            {
                _retries += 1;
                if (_quicFailed && !_fellBackHttp2)
                {
                    _fellBackHttp2 = true;
                    _useHttp2 = true;
                    Log(LogPrefix + " QUIC appears blocked; retrying over HTTP/2");
                    detail = "QUIC appears blocked; retrying over HTTP/2 — " + detail;
                }
                _retryAtMs = NowMs + (long)(RetryBackoffSec * 1000.0 * (1 << (_retries - 1)));
                ClearProcess();
                Url = "";
                SetState("starting", string.Format("retry {0}/{1}: {2}", _retries, MaxRetries, detail));
                return;
            }
            var was = State;
            KillInternal(false);
            if (was == "ready" || was == "lost")
                SetState("lost", detail);
            else
                Fail(detail);
        }

        bool WantHttp2Fallback(long now)
        {
            if (!ProtocolFallback || _fellBackHttp2 || HasProtocolArg())
                return false;
            if (_quicFailed)
                return true;
            return Url != "" && !_registered && now - _startedMs > (long)(ProtocolFallbackSec * 1000.0);
        }

        bool HasProtocolArg()
        {
            if (ExtraArgs == null)
                return false;
            foreach (var a in ExtraArgs)
            {
                if (a == "--protocol" || (a != null && a.StartsWith("--protocol=", StringComparison.Ordinal)))
                    return true;
            }
            return false;
        }

        void FallBackHttp2()
        {
            _fellBackHttp2 = true;
            _useHttp2 = true;
            Log(LogPrefix + " QUIC (UDP 7844) looks blocked; retrying over HTTP/2 (TCP 443)");
            ClearProcess();
            Launch(_bin);
        }

        // --- teardown -------------------------------------------------------------------

        /// <summary>Kills the child process and releases its handles/pid bookkeeping. State and Url untouched.</summary>
        void ClearProcess()
        {
            var wasPid = _pid;
            _gen++; // everything still queued for this process is stale
            var proc = _proc;
            _proc = null;
            _pid = -1;
            if (proc != null)
            {
                try
                {
                    if (!proc.HasExited)
                        proc.Kill(true);
                }
                catch
                {
                    try { proc.Kill(); }
                    catch { }
                }
                JoinQuiet(_watcher); // the watcher joins the readers after WaitForExit
                try { proc.Dispose(); }
                catch { }
            }
            _readerErr = _readerOut = _watcher = null;
            lock (s_livePidsLock)
                s_livePids.Remove(wasPid);
            RemovePidfile(wasPid);
        }

        void KillInternal(bool silent)
        {
            ClearProcess();
            _retryAtMs = 0;
            CancelDownload();
            StopDns();
            Url = "";
            _registered = false;
            _conns.Clear();
            _connsLostMs = 0;
            _deadlineMs = 0;
            _downloadThenStart = false;
            if (silent)
                State = "stopped";
        }

        void Fail(string reason)
        {
            Url = "";
            LastError = reason;
            SetState("failed", reason);
        }

        void SetState(string newState, string detail)
        {
            State = newState;
            // On the pump thread (Start/Stop/Pump all run there) emit inline — the same observable
            // ordering as the Godot signal. From any other thread the emission is marshalled so
            // subscribers always observe it from Pump()'s thread.
            if (_pumpThreadId != 0 && Environment.CurrentManagedThreadId == _pumpThreadId)
            {
                var h0 = StateChanged;
                if (h0 != null)
                    h0(newState, detail);
                return;
            }
            var gen = _gen;
            Post(gen, () =>
            {
                var h = StateChanged;
                if (h != null)
                    h(newState, detail);
            });
        }

        string ErrSuffix()
        {
            return _lastErrLine == "" ? "" : ": " + FriendlyError(_lastErrLine);
        }

        void Log(string line)
        {
            LogLines.Add(line);
            if (LogLines.Count > MaxLogLines)
                LogLines.RemoveAt(0);
        }

        // --- DNS readiness --------------------------------------------------------------

        void StartDnsWait(string host, int gen)
        {
            StopDns();
            var cts = new CancellationTokenSource();
            _dnsCts = cts;
            var started = _dnsStartedMs;
            var th = new Thread(() => DnsLoop(host, started, gen, cts.Token)) { IsBackground = true, Name = "PmcTunnel-dns" };
            th.Start();
        }

        void DnsLoop(string host, long startedMs, int gen, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                var remain = (long)(DnsTimeoutSec * 1000.0) - (NowMs - startedMs);
                if (remain <= 0)
                {
                    Post(gen, OnDnsTimeout);
                    return;
                }
                var ok = false;
                try
                {
                    ok = ProbeDns(host, ct, remain);
                }
                catch { }
                if (ok)
                {
                    Post(gen, OnDnsOk);
                    return;
                }
                try { System.Threading.Tasks.Task.Delay(1000, ct).Wait(ct); }
                catch { return; }
            }
        }

        /// <summary>One DNS-over-HTTPS attempt (or the injected <see cref="DnsProbe"/>), bounded by 5 s and
        /// the remaining DNS timeout.</summary>
        bool ProbeDns(string host, CancellationToken ct, long remainMs)
        {
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                cts.CancelAfter((int)Math.Min(5000, Math.Max(1, remainMs)));
                try
                {
                    if (DnsProbe != null)
                        return DnsProbe(host, cts.Token).GetAwaiter().GetResult();
                    var q = DnsOverHttpsUrl + "?name=" + host + "&type=A";
                    using (var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, q))
                    {
                        req.Headers.Accept.ParseAdd("application/dns-json");
                        using (var resp = SharedHttp.SendAsync(req, cts.Token).GetAwaiter().GetResult())
                        {
                            if (!resp.IsSuccessStatusCode)
                                return false;
                            var body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                            var data = JObject.Parse(body);
                            var answers = data["Answer"] as JArray;
                            return (int?)data["Status"] == 0 && answers != null && answers.Count > 0;
                        }
                    }
                }
                catch
                {
                    return false;
                }
            }
        }

        void OnDnsOk()
        {
            if (State != "starting" || _dnsStartedMs <= 0)
                return;
            Log(LogPrefix + " DNS resolves after " + (NowMs - _dnsStartedMs) + " ms");
            _dnsStartedMs = -1;
            StopDns();
            SetState("ready", Url);
        }

        void OnDnsTimeout()
        {
            if (State != "starting" || _dnsStartedMs <= 0)
                return;
            Log(LogPrefix + " DNS check timed out; reporting ready anyway");
            _dnsStartedMs = -1;
            StopDns();
            SetState("ready", Url);
        }

        void StopDns()
        {
            var c = _dnsCts;
            _dnsCts = null;
            if (c != null)
            {
                try { c.Cancel(); }
                catch { }
                c.Dispose();
            }
        }
    }
}
