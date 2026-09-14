using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Splatter.Pmc {
    /// <summary>
    /// Hosts phone controllers: an HTTP/1.1 + WebSocket server on one TCP port, plus player sessions.
    /// Port of host.gd — engine-free; <see cref="PmcHost"/> (Unity glue) mirrors the settings and calls
    /// <see cref="Poll"/> from <c>Update</c>.
    ///
    /// Unlike the Godot original, socket I/O always runs on a dedicated worker thread
    /// (<see cref="PmcIoWorker"/>): accept, read, write and HTTP/WS frame decode happen there, and
    /// complete events are applied inside <see cref="Poll"/> on the caller's thread. All public events
    /// fire only inside <see cref="Poll"/> (or inside the synchronous <see cref="Start"/>/<see cref="Stop"/>/
    /// setter calls, matching the Godot signal semantics).
    /// </summary>
    public sealed class PmcHostCore : IDisposable {
        /// <summary>Wire protocol version (the <c>sdk</c> field of <c>pmc.hello</c>).</summary>
        public const int SdkVersion = 1;
        /// <summary>Package version.</summary>
        public const string Version = "0.1.0";
        /// <summary>Longest accepted display name.</summary>
        public const int MaxNameLength = 32;

        // ---------------------------------------------------------------------------------------------
        // Events (raised on the thread calling Poll; lifecycle events fire inside Start/Stop)

        /// <summary>After the server starts listening, with the actual port.</summary>
        public event Action<int> Started;
        /// <summary>After <see cref="Stop"/>.</summary>
        public event Action Stopped;
        /// <summary>A new player completed the hello handshake. Also fires when a tombstoned token
        /// comes back (same id and meta).</summary>
        public event Action<PmcPlayer> PlayerJoined;
        /// <summary>A known player reconnected within the grace period (or replaced its own socket).</summary>
        public event Action<PmcPlayer> PlayerRejoined;
        /// <summary>A player's socket closed. They stay in <see cref="Players"/> until
        /// <see cref="GraceSeconds"/> runs out.</summary>
        public event Action<PmcPlayer> PlayerDisconnected;
        /// <summary>A player was removed. Reason is "timeout", "kicked" or "leave".</summary>
        public event Action<PmcPlayer, string> PlayerLeft;
        /// <summary>A player's name or profile changed.</summary>
        public event Action<PmcPlayer> PlayerUpdated;
        /// <summary>A player sent the correct <see cref="AdminPin"/>.</summary>
        public event Action<PmcPlayer> AdminAuthenticated;
        /// <summary>A game message: the <c>d</c> value of a <c>msg</c> frame as a
        /// <see cref="JToken"/>, or a <see cref="byte[]"/> for binary frames.</summary>
        public event Action<PmcPlayer, object> MessageReceived;
        /// <summary>The join URL changed (start, port, <see cref="AdvertiseUrl"/>,
        /// <see cref="JoinCode"/>, tunnel).</summary>
        public event Action<string> JoinUrlChanged;
        /// <summary>Once, when no phone has joined within <see cref="NoJoinsHintSeconds"/> of the join
        /// URL being shown.</summary>
        public event Action NoJoinsHint;
        /// <summary>Tunnel progress: "downloading", "starting", "ready" (url = public URL), "lost"
        /// (url = reason), "failed" (url = reason) or "stopped".</summary>
        public event Action<string, string> TunnelStateChanged;

        // ---------------------------------------------------------------------------------------------
        // Settings (set before Start)

        /// <summary>Preferred TCP port. 0 picks a free ephemeral port.</summary>
        public int Port { get; set; } = 8080;
        /// <summary>If <see cref="Port"/> is busy, try up to this many ports above it.</summary>
        public int PortSearch { get; set; } = 20;
        /// <summary>Interface to bind: "*" for all, or an IP address.</summary>
        public string BindAddress { get; set; } = "*";
        /// <summary>Directory served at "/" (index.html by default). "" serves nothing.</summary>
        public string ControllerDir { get; set; } = "";
        /// <summary>Directory the JS SDK is served from at /pmc/ (pmc.js, pmc.d.ts). "" disables
        /// those routes (404). The Unity glue points this at the package's Web/ dir.</summary>
        public string WebDir { get; set; } = "";
        /// <summary>When non-empty, new players must send this code (case-insensitive). It's included
        /// in <see cref="JoinUrl"/> as ?code=.</summary>
        public string JoinCode {
            get { return _joinCode; }
            set {
                if (value == _joinCode) return;
                _joinCode = value ?? "";
                UrlChanged();
            }
        }
        /// <summary>Maximum players, including those in their grace period. 0 means unlimited.</summary>
        public int MaxPlayers { get; set; } = 0;
        /// <summary>Seconds a disconnected player keeps their slot before <see cref="PlayerLeft"/>
        /// with "timeout".</summary>
        public float GraceSeconds { get; set; } = 30f;
        /// <summary>Seconds a timed-out player's token is remembered, so the same id and meta come
        /// back on reconnect.</summary>
        public float RememberSeconds { get; set; } = 3600f;
        /// <summary>WebSocket ping interval. A socket is closed after 2 intervals with no inbound
        /// frame. 0 disables it.</summary>
        public float HeartbeatSeconds { get; set; } = 15f;
        /// <summary>PIN for <c>pmc.auth</c> admin elevation. Empty means admin auth always fails.</summary>
        public string AdminPin { get; set; } = "";
        /// <summary>Overrides the join URL base. Empty uses the best LAN IPv4 (or the tunnel URL).</summary>
        public string AdvertiseUrl {
            get { return _advertiseUrl; }
            set {
                if (value == _advertiseUrl) return;
                _advertiseUrl = value ?? "";
                UrlChanged();
            }
        }
        /// <summary>Largest accepted WebSocket message (after reassembly).</summary>
        public int MaxMessageBytes { get; set; } = 1 << 20;
        /// <summary>When &gt; 0, <see cref="NoJoinsHint"/> fires once if no player has joined this many
        /// seconds after the join URL went up.</summary>
        public float NoJoinsHintSeconds { get; set; } = 0f;
        /// <summary>Maximum time spent draining queued I/O events per <see cref="Poll"/>.</summary>
        public float IoBudgetMsec { get; set; } = 8f;
        /// <summary>Maximum simultaneous TCP connections. Extra ones are closed on accept.
        /// 0 = unlimited.</summary>
        public int MaxConnections { get; set; } = 0;
        /// <summary>Seconds a client has to send a complete request head (slowloris protection). Idle
        /// keep-alive sockets close after it too.</summary>
        public float HeaderTimeoutSeconds { get; set; } = 10f;
        /// <summary>Largest request head (request line + headers).</summary>
        public int MaxHeaderBytes { get; set; } = 16384;
        /// <summary>Largest HTTP request body (for <see cref="AddRoute"/> handlers).</summary>
        public int MaxBodyBytes { get; set; } = 65536;
        /// <summary>Seconds a WebSocket has to send <c>pmc.hello</c>.</summary>
        public float HelloTimeoutSeconds { get; set; } = 10f;
        /// <summary>Unsent bytes allowed per connection before it's dropped as too slow.</summary>
        public int MaxBacklogBytes { get; set; } = 16 << 20;
        /// <summary>Maximum simultaneous connections from one client address. 0 means unlimited.
        /// Behind a tunnel the address comes from CF-Connecting-IP.</summary>
        public int MaxConnectionsPerAddress { get; set; } = 8;
        /// <summary>Wrong join codes an address may send (within <see cref="JoinCodeBlockSeconds"/>)
        /// before it's blocked.</summary>
        public int JoinCodeMaxFailures { get; set; } = 10;
        /// <summary>How long an address is blocked after too many wrong join codes.</summary>
        public float JoinCodeBlockSeconds { get; set; } = 60f;
        /// <summary>Wrong admin PIN attempts across the whole host before the PIN is disabled until
        /// restart (0 = unlimited).</summary>
        public int AdminPinMaxFailures { get; set; } = 20;
        /// <summary>When on, a WebSocket upgrade with an Origin header not in
        /// <see cref="AllowedOrigins"/> is refused with 403.</summary>
        public bool CheckOrigin { get; set; }
        /// <summary>Origin header values allowed to open the WebSocket when <see cref="CheckOrigin"/>
        /// is on. Exact match on scheme://host[:port].</summary>
        public List<string> AllowedOrigins { get; } = new List<string>();

        // ---- tunnel settings (applied pre-launch; see PmcTunnel) ----

        /// <summary>Let <see cref="StartTunnel"/> download cloudflared if it isn't found.</summary>
        public bool TunnelAllowDownload { get; set; }
        /// <summary>Explicit cloudflared executable path (optional).</summary>
        public string CloudflaredPath { get; set; } = "";
        /// <summary>"quick" (account-less, random trycloudflare URL) or "named".</summary>
        public string TunnelMode { get; set; } = "quick";
        /// <summary>Named mode: token from the Cloudflare dashboard.</summary>
        public string NamedTunnelToken { get; set; } = "";
        /// <summary>Named mode: the public hostname routed to the tunnel.</summary>
        public string NamedTunnelHostname { get; set; } = "";
        /// <summary>Named mode: the tunnel name (generated-config variant).</summary>
        public string NamedTunnelName { get; set; } = "";
        /// <summary>Named mode: credentials file for the generated-config variant.</summary>
        public string NamedTunnelCredentialsFile { get; set; } = "";
        /// <summary>Wait for the public hostname to resolve before the tunnel reports ready.</summary>
        public bool TunnelVerifyDns { get; set; } = true;
        /// <summary>Seconds to wait for the tunnel to become ready before failing.</summary>
        public float TunnelReadyTimeoutSec { get; set; } = 60f;
        /// <summary>Extra cloudflared arguments.</summary>
        public string[] TunnelExtraArgs { get; set; } = new string[0];
        /// <summary>Join code used while a tunnel is up (<see cref="StartTunnel"/>'s code parameter
        /// wins). Non-empty is used as-is and is never auto-cleared on <see cref="StopTunnel"/>.</summary>
        public string TunnelJoinCode { get; set; } = "";
        /// <summary>Automatically restart a tunnel that dropped after being ready. Bounded — see
        /// <see cref="MaxAutoRestarts"/>.</summary>
        public bool TunnelAutoRestart { get; set; } = true;
        /// <summary>Delay before an automatic tunnel restart.</summary>
        public float TunnelRestartDelaySec { get; set; } = 2f;

        /// <summary>Supplies the PNG for /pmc/qr.png (text, modulePx, quiet modules) → PNG bytes.
        /// Wire it to <c>PmcQr.EncodePng</c> when the QR module is present; null → 501 like the Godot
        /// host without its PMCQr class.</summary>
        public static Func<string, int, int, byte[]> QrPngProvider;

        // ---------------------------------------------------------------------------------------------
        // Constants

        private const int TimerMsec = 50;
        private const int StallMsec = 30000;
        private const int CloseHandshakeMsec = 1000;
        private const int AuthMaxFailures = 5;
        private const int AuthLockMsec = 30000;
        private const int AuthAddrMaxFailures = 20;
        private const int AuthAddrLockMsec = 60000;
        private const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ"; // 24 chars, no ambiguous
        private const int MaxAutoRestarts = 3;

        // ---------------------------------------------------------------------------------------------
        // State

        private TcpListener _server;
        private bool _running;
        private bool _disposed;
        private int _port;
        private readonly List<PmcConnection> _conns = new List<PmcConnection>();
        private readonly Dictionary<string, int> _addrConns = new Dictionary<string, int>();
        private readonly Dictionary<string, AddrFailure> _addrFailures = new Dictionary<string, AddrFailure>();
        private long _bytesIn;
        private long _bytesOut;
        private long _msgsIn;
        private long _msgsOut;
        private long _lastTimerMsec;
        private bool _closedPending;
        private readonly Dictionary<int, PmcPlayer> _players = new Dictionary<int, PmcPlayer>();
        private readonly Dictionary<string, PmcPlayer> _byToken = new Dictionary<string, PmcPlayer>();
        private readonly Dictionary<string, Tombstone> _tombstones = new Dictionary<string, Tombstone>();
        private readonly HashSet<string> _banned = new HashSet<string>();
        private int _nextId = 1;
        private readonly List<RouteEntry> _routes = new List<RouteEntry>();
        private readonly List<MountEntry> _mounts = new List<MountEntry>();
        private long _lastSweepMsec;
        private readonly List<LanAddress> _lanCache = new List<LanAddress>();
        private long _lanCacheMsec = -100000;
        private string _joinCode = "";
        private string _advertiseUrl = "";
        private bool _suppressUrlSignal;
        private PmcTunnel _tunnel;
        private PmcTunnel _tunnelPending;      // replacement tunnel coming up (rolling restart)
        private string _pendingTunnelCode = ""; // game-supplied code for the next "ready"
        private string _tunnelPrevAdvertise = "";
        private bool _tunnelSetAdvertise;
        private bool _tunnelGeneratedCode;
        private int _tunnelLocalPort = -1;      // last port passed to _tunnel.Start
        private int _tunnelPendingLocalPort = -1;
        private int _authFailTotal;
        private bool _authDisabled;
        private long _joinCount;             // hellos that produced a welcome
        private long _hintDeadlineMsec;      // armed deadline for NoJoinsHint (0 = disarmed)
        private long _hintJoinsAtArm;
        private bool _hintFired;
        private PmcIoWorker _io;
        private Thread _ioThread;
        private int _autoRestarts;
        private int _tunnelEpoch;
        private long _autoRestartAtMsec;      // deferred auto-restart (0 = none)
        private int _autoRestartEpoch;
        private int _autoRestartCount;
        private long _httpRequests;
        private long _accepted;
        private long _refused;
        private long _lastPollUsec;
        private long _maxPollUsec;

        // Tunnel state changes arrive on the tunnel's threads; they cross to Poll here.
        private readonly List<TunnelEvent> _tunnelEvents = new List<TunnelEvent>();

        // Tunnels outliving their host (Stop keeps them; a discarded host detaches them here so the
        // next StartTunnel can re-adopt). Static because the new host is a different instance.
        private static readonly List<DetachedTunnel> Detached = new List<DetachedTunnel>();

        private sealed class AddrFailure {
            public int Count;
            public long WindowEndMsec;
            public long BlockedUntilMsec;
        }

        private sealed class Tombstone {
            public int Id;
            public string Name;
            public JObject Profile;
            public JObject Meta;
            public long ExpiresMsec;
        }

        private sealed class RouteEntry {
            public string Prefix;
            public Func<PmcHttpRequest, PmcHttpResponse> Handler;
        }

        private sealed class MountEntry {
            public string Prefix;
            public string Dir;
            public bool PlayersOnly;
        }

        private struct LanAddress {
            public string Name;
            public string Address;
            public int Score;
        }

        private struct TunnelEvent {
            public bool Pending;
            public string State;
            public string Detail;
        }

        private sealed class DetachedTunnel {
            public PmcTunnel Tunnel;
            public int LocalPort;
        }

        // ---------------------------------------------------------------------------------------------
        // Lifecycle

        /// <summary>Starts listening on <see cref="Port"/> (or the next free port, up to
        /// <see cref="PortSearch"/> above it). Returns 0, or a non-zero error code.</summary>
        public int Start() {
            if (_running) return 0;
            if (_disposed) return 1;
            _server = null;
            int err = 1;
            int tries = Port == 0 ? 1 : Math.Max(0, PortSearch) + 1;
            for (int i = 0; i < tries; i++) {
                int p = Port + i;
                if (p > 65535) break;
                if (p != 0 && BindAddress == "*" && !Ipv4PortFree(p)) {
                    // "*" binds a dual-stack socket, which can succeed even while another process owns
                    // the port on IPv4 (e.g. Windows netsh portproxy). IPv4 clients would reach that
                    // process.
                    err = 32; // already in use
                    continue;
                }
                var listener = TryListen(p, out int lerr);
                if (listener != null) {
                    _server = listener;
                    err = 0;
                    break;
                }
                err = lerr;
            }
            if (_server == null) {
                return err;
            }
            _port = ((IPEndPoint)_server.LocalEndpoint).Port;
            _running = true;
            _lastSweepMsec = PmcTime.NowMsec();
            StartIoThread();
            ArmHint();
            // A tunnel kept across Stop()->Start() points at the old port; retarget it when that
            // changed. Before the JoinUrlChanged below so a stale tunnel URL stays unannounced.
            if (_tunnel != null && _tunnelLocalPort >= 0 && _tunnelLocalPort != _port) {
                RestoreAfterTunnel(false);
                try {
                    _tunnel.Start(_port);
                } catch (Exception) {
                    // the tunnel reports through StateChanged
                }
                _tunnelLocalPort = _port;
            }
            Started?.Invoke(_port);
            JoinUrlChanged?.Invoke(JoinUrl());
            return 0;
        }

        private TcpListener TryListen(int p, out int err) {
            err = 1;
            IPAddress ip;
            bool dual = false;
            if (BindAddress == "*") {
                ip = IPAddress.IPv6Any;
                dual = true;
            } else {
                try {
                    ip = IPAddress.Parse(BindAddress);
                } catch (Exception) {
                    err = 22; // invalid argument
                    return null;
                }
            }
            try {
                var l = new TcpListener(ip, p);
                if (dual) {
                    try {
                        l.Server.DualMode = true;
                    } catch (Exception) {
                        // No IPv6: fall back to a plain IPv4 any-bind.
                        l = new TcpListener(IPAddress.Any, p);
                    }
                }
                l.Start();
                return l;
            } catch (SocketException se) {
                err = se.SocketErrorCode == SocketError.AddressAlreadyInUse ? 32 : 1;
                return null;
            } catch (Exception) {
                err = 1;
                return null;
            }
        }

        private static bool Ipv4PortFree(int p) {
            try {
                var probe = new TcpListener(IPAddress.Any, p);
                probe.Start();
                probe.Stop();
                return true;
            } catch (Exception) {
                return false;
            }
        }

        /// <summary>Stops the server, closes every socket (WebSocket close 1001) and forgets all
        /// players, tombstones and bans. No player events are raised. A running tunnel is left up so
        /// <see cref="Start"/> on the same port reuses its URL — call <see cref="StopTunnel"/> first
        /// to take it down too.</summary>
        public void Stop() {
            if (!_running) return;
            Shutdown(true);
            Stopped?.Invoke();
        }

        /// <summary>Stops the server AND kills the tunnel (unlike <see cref="Stop"/>, which keeps a
        /// live tunnel for the next <see cref="Start"/>).</summary>
        public void Dispose() {
            if (_disposed) return;
            _disposed = true;
            CancelPending();
            if (_tunnel != null) {
                var t = _tunnel;
                _tunnel = null;
                try {
                    t.StateChanged -= OnTunnelStateMain;
                    t.Stop();
                } catch (Exception) {
                }
            }
            Shutdown(true);
        }

        /// <summary>Moves a live tunnel to the detached registry (it keeps serving) so a later
        /// <see cref="StartTunnel"/> — even on a new PmcHostCore — re-adopts it. Returns false when
        /// there's nothing live to detach.</summary>
        public bool DetachTunnel() {
            var t = _tunnel;
            _tunnel = null;
            if (t == null) return false;
            try {
                t.StateChanged -= OnTunnelStateMain;
            } catch (Exception) {
            }
            bool live = PmcTunnelSeam.IsProcessAlive(t)
                || t.State == "ready" || t.State == "starting" || t.State == "lost";
            if (!live) {
                try { t.Stop(); } catch (Exception) { }
                return false;
            }
            PmcTunnelSeam.SetDetached(t, true);
            lock (Detached) {
                Detached.Add(new DetachedTunnel { Tunnel = t, LocalPort = _tunnelLocalPort });
            }
            return true;
        }

        private void Shutdown(bool graceful) {
            CancelPending();
            if (_tunnel != null && !graceful) {
                // Non-graceful teardown (host object discarded): detach so a replacement can adopt.
                DetachTunnel();
            }
            if (!_running) return;
            _running = false;
            StopIoThread();
            foreach (var c in _conns) {
                if (graceful && c.Mode == PmcConnection.ConnMode.Ws && !c.CloseSent) {
                    c.Queue(PmcWsFrame.Close(1001, "server stopping"));
                    c.Flush(65536);
                }
                c.CloseNow("stopped");
            }
            _conns.Clear();
            foreach (var p in _players.Values) {
                p.Conn = null;
                p.Connected = false;
            }
            _players.Clear();
            _byToken.Clear();
            _tombstones.Clear();
            _banned.Clear();
            _addrConns.Clear();
            _addrFailures.Clear();
            _authFailTotal = 0;
            _authDisabled = false;
            _hintDeadlineMsec = 0;
            _autoRestartAtMsec = 0;
            if (_server != null) {
                _server.Stop();
                _server = null;
            }
        }

        /// <summary>Whether the server is listening.</summary>
        public bool Running { get { return _running; } }

        /// <summary>The port actually bound, or <see cref="Port"/> when not running.</summary>
        public int BoundPort { get { return _running ? _port : Port; } }

        /// <summary>Counters for diagnostics: connections, websockets, players, http_requests,
        /// ws_messages_in/out, bytes_in/out, last_poll_usec, max_poll_usec, accepted, refused.</summary>
        public JObject GetStats() {
            var d = new JObject();
            d["http_requests"] = _httpRequests;
            d["ws_messages_in"] = _msgsIn;
            d["ws_messages_out"] = _msgsOut;
            d["bytes_in"] = _bytesIn;
            d["bytes_out"] = _bytesOut;
            d["last_poll_usec"] = _lastPollUsec;
            d["max_poll_usec"] = _maxPollUsec;
            d["accepted"] = _accepted;
            d["refused"] = _refused;
            long ws = 0;
            foreach (var c in _conns) {
                if (c.Mode == PmcConnection.ConnMode.Ws) ws += 1;
            }
            d["connections"] = _conns.Count;
            d["websockets"] = ws;
            d["players"] = _players.Count;
            return d;
        }

        /// <summary>Resets <c>max_poll_usec</c> in <see cref="GetStats"/>.</summary>
        public void ResetPollStats() {
            _maxPollUsec = 0;
        }

        // ---------------------------------------------------------------------------------------------
        // Join URL / LAN

        /// <summary>The URL players open: <see cref="AdvertiseUrl"/> (or the tunnel URL), or else
        /// http://&lt;best LAN IPv4&gt;:&lt;port&gt;/, with ?code= added when <see cref="JoinCode"/>
        /// is set.</summary>
        public string JoinUrl() {
            string baseUrl = (_advertiseUrl ?? "").Trim();
            if (baseUrl == "") {
                var addrs = LanAddresses();
                string hostIp = addrs.Count > 0 ? addrs[0] : "127.0.0.1";
                baseUrl = "http://" + hostIp + ":" + BoundPort + "/";
            }
            if (baseUrl.IndexOf("://", StringComparison.Ordinal) < 0) {
                baseUrl = "http://" + baseUrl;
            }
            int schemeEnd = baseUrl.IndexOf("://", StringComparison.Ordinal) + 3;
            if (baseUrl.IndexOf('/', schemeEnd) < 0) {
                int q = baseUrl.IndexOf('?', schemeEnd);
                baseUrl = q < 0 ? baseUrl + "/" : baseUrl.Substring(0, q) + "/" + baseUrl.Substring(q);
            }
            if (_joinCode != "") {
                baseUrl += (baseUrl.IndexOf('?') >= 0 ? "&" : "?") + "code=" + Uri.EscapeDataString(_joinCode);
            }
            return baseUrl;
        }

        /// <summary>The best LAN IPv4 address, or "127.0.0.1".</summary>
        public string LocalIp {
            get {
                var a = LanAddresses();
                return a.Count > 0 ? a[0] : "127.0.0.1";
            }
        }

        /// <summary>Local IPv4 addresses, best first. Private addresses on physical Ethernet/Wi-Fi
        /// adapters rank above virtual, VPN and container adapters. Loopback is excluded. Cached for
        /// 30 seconds.</summary>
        public List<string> LanAddresses() {
            long now = PmcTime.NowMsec();
            if (now - _lanCacheMsec >= 30000) {
                _lanCache.Clear();
                try {
                    foreach (var iface in NetworkInterface.GetAllNetworkInterfaces()) {
                        string friendly = iface.Name ?? "";
                        string desc = iface.Description ?? "";
                        string name = desc != "" ? desc : friendly;
                        var props = iface.GetIPProperties();
                        if (props == null) continue;
                        foreach (var ua in props.UnicastAddresses) {
                            var addr = ua.Address;
                            if (addr.AddressFamily != AddressFamily.InterNetwork) continue;
                            string a = addr.ToString();
                            if (a.StartsWith("127.", StringComparison.Ordinal)) continue;
                            string scoreName = name;
                            if (friendly != "" && friendly != name) scoreName = name + " " + friendly;
                            _lanCache.Add(new LanAddress { Name = name, Address = a, Score = ScoreAddress(scoreName, a) });
                        }
                    }
                } catch (Exception) {
                    // network interface enumeration unavailable — empty list
                }
                _lanCache.Sort(delegate (LanAddress x, LanAddress y) {
                    if (x.Score != y.Score) return y.Score.CompareTo(x.Score);
                    return string.CompareOrdinal(x.Address, y.Address);
                });
                _lanCacheMsec = now;
            }
            var outp = new List<string>(_lanCache.Count);
            foreach (var e in _lanCache) outp.Add(e.Address);
            return outp;
        }

        private static readonly string[] VirtualHints = {
            "vethernet", "wsl", "hyper-v", "virtualbox", "vbox", "vmware", "vmnet", "tailscale",
            "zerotier", "docker", "vpn", "loopback", "wireguard", "hamachi", "npcap", "bluetooth",
            "pseudo", "teredo", "isatap", "nordlynx", "parallels", "virtual", "tunnel", "radmin"
        };
        private static readonly string[] VirtualPrefixes = {
            "wg", "tun", "tap", "utun", "br-", "veth", "virbr", "vnic", "zt", "lo", "awdl", "llw",
            "anpi", "bridge", "ipsec", "ppp", "gif", "stf", "cni", "flannel", "cali", "vboxnet",
            "lxc", "lxd", "incus"
        };
        private static readonly string[] PhysicalHints = {
            "ethernet", "wi-fi", "wifi", "wlan", "wireless", "local area connection"
        };
        private static readonly string[] PhysicalPrefixes = {
            "eth", "en", "wl", "wlan", "wlp", "enp", "eno", "ens"
        };

        /// <summary>Scores an IPv4 <paramref name="address"/> on an adapter called
        /// <paramref name="ifaceName"/> (higher is better). Used by <see cref="LanAddresses"/>.</summary>
        public static int ScoreAddress(string ifaceName, string address) {
            if (!IsIpv4(address)) return -1000;
            var o = address.Split('.');
            int a = ParseInt(o[0]);
            int b = ParseInt(o[1]);
            int c = ParseInt(o[2]);
            int s;
            if (a == 127) return -1000;
            if (a == 169 && b == 254) {
                s = -300;
            } else if (a == 10 || (a == 192 && b == 168) || (a == 172 && b >= 16 && b <= 31)) {
                s = 100;
            } else if (a == 100 && b >= 64 && b <= 127) {
                s = 20; // CGNAT range, used by Tailscale
            } else {
                s = 50;
            }
            string n = (ifaceName ?? "").ToLowerInvariant();
            bool isVirtual = false;
            foreach (string h in VirtualHints) {
                if (n.IndexOf(h, StringComparison.Ordinal) >= 0) {
                    isVirtual = true;
                    break;
                }
            }
            if (!isVirtual) {
                foreach (string word in n.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)) {
                    foreach (string pre in VirtualPrefixes) {
                        if (word.StartsWith(pre, StringComparison.Ordinal)
                            && !(pre == "lo" && word.StartsWith("local", StringComparison.Ordinal))) {
                            isVirtual = true;
                            break;
                        }
                    }
                    if (isVirtual) break;
                }
            }
            if (isVirtual) {
                s -= 200;
            } else {
                bool physical = false;
                foreach (string h in PhysicalHints) {
                    if (n.IndexOf(h, StringComparison.Ordinal) >= 0) physical = true;
                }
                foreach (string word in n.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)) {
                    foreach (string pre in PhysicalPrefixes) {
                        if (word.StartsWith(pre, StringComparison.Ordinal)) physical = true;
                    }
                }
                if (physical) s += 20;
            }
            if (a == 192 && b == 168 && c == 56) s -= 40; // VirtualBox host-only default
            if (a == 172 && b == 17) s -= 40;             // Docker default bridge
            return s;
        }

        private static bool IsIpv4(string a) {
            IPAddress ip;
            return IPAddress.TryParse(a, out ip) && ip.AddressFamily == AddressFamily.InterNetwork;
        }

        private static int ParseInt(string s) {
            int v;
            return int.TryParse(s, out v) ? v : 0;
        }

        // ---------------------------------------------------------------------------------------------
        // Players

        /// <summary>Players ordered by id. With <paramref name="includeDisconnected"/> false, only
        /// players with a live socket are included.</summary>
        public List<PmcPlayer> Players(bool includeDisconnected = true) {
            var outp = new List<PmcPlayer>(_players.Count);
            foreach (var p in _players.Values) {
                if (includeDisconnected || p.Connected) outp.Add(p);
            }
            outp.Sort(delegate (PmcPlayer x, PmcPlayer y) { return x.Id.CompareTo(y.Id); });
            return outp;
        }

        /// <summary>The player with <paramref name="id"/>, or null.</summary>
        public PmcPlayer GetPlayer(int id) {
            PmcPlayer p;
            return _players.TryGetValue(id, out p) ? p : null;
        }

        /// <summary>Sends to one player. A <see cref="byte[]"/> goes out as a binary frame; a
        /// <see cref="JToken"/>/<see cref="System.Collections.IDictionary"/>/<see cref="System.Collections.IList"/>/
        /// string/number is JSON-encoded as <c>{"t":"msg","d":data}</c>. Players without a socket are
        /// skipped.</summary>
        public void Send(PmcPlayer to, object data) {
            var p = Resolve(to);
            if (p == null || p.Conn == null) return;
            var c = p.Conn;
            if (c.CloseSent || !c.IsOpen()) return;
            c.Queue(EncodeMsg(data));
            _msgsOut += 1;
        }

        /// <summary><see cref="Send(PmcPlayer, object)"/> by id.</summary>
        public void Send(int id, object data) {
            Send(GetPlayer(id), data);
        }

        /// <summary>Sends to every connected player, or only those where <paramref name="filter"/>
        /// returns true. The frame is encoded once.</summary>
        public void Broadcast(object data, Func<PmcPlayer, bool> filter = null) {
            byte[] frame = null;
            foreach (var p in _players.Values) {
                if (p.Conn == null) continue;
                var c = p.Conn;
                if (c.CloseSent || !c.IsOpen()) continue;
                if (filter != null && !filter(p)) continue;
                if (frame == null) frame = EncodeMsg(data);
                c.Queue(frame);
                _msgsOut += 1;
            }
        }

        /// <summary>Removes a player: sends <c>pmc.kicked</c>, closes with 4001 and raises
        /// <see cref="PlayerLeft"/> with "kicked". No tombstone is kept by default: a kicked token that
        /// reconnects becomes a new player (new id, empty meta). With <paramref name="remember"/>, a
        /// tombstone is left (subject to <see cref="RememberSeconds"/>) so the same token rejoins with
        /// its id and meta intact. With <paramref name="ban"/>, the token is refused (<c>banned</c>)
        /// until the host stops or <see cref="ClearBans"/> runs.</summary>
        public void Kick(PmcPlayer to, string reason = "", bool ban = false, bool remember = false) {
            var p = Resolve(to);
            if (p == null) return;
            if (ban) _banned.Add(p.Token);
            var c = p.Conn;
            if (c != null) {
                Detach(c);
                var msg = new JObject { ["t"] = "pmc.kicked", ["reason"] = reason };
                SendJson(c, msg);
                WsClose(c, 4001, "kicked");
            }
            RemovePlayer(p, "kicked", remember);
        }

        /// <summary><see cref="Kick(PmcPlayer, string, bool, bool)"/> by id.</summary>
        public void Kick(int id, string reason = "", bool ban = false, bool remember = false) {
            Kick(GetPlayer(id), reason, ban, remember);
        }

        /// <summary>Forgets all bans made with <see cref="Kick"/>.</summary>
        public void ClearBans() {
            _banned.Clear();
        }

        private PmcPlayer Resolve(PmcPlayer to) {
            if (to == null) return null;
            PmcPlayer cur;
            return _players.TryGetValue(to.Id, out cur) && ReferenceEquals(cur, to) ? to : null;
        }

        // ---------------------------------------------------------------------------------------------
        // HTTP routing

        /// <summary>Registers <paramref name="handler"/> for request paths starting with
        /// <paramref name="prefix"/> (longest prefix wins). Return null to fall through to static
        /// files. Handlers see any method. <see cref="PmcHttpRequest.SubPath"/> is the path after the
        /// prefix. Paths under /pmc/ are reserved.</summary>
        public void AddRoute(string prefix, Func<PmcHttpRequest, PmcHttpResponse> handler) {
            _routes.RemoveAll(r => r.Prefix == prefix);
            _routes.Add(new RouteEntry { Prefix = prefix, Handler = handler });
            _routes.Sort(delegate (RouteEntry x, RouteEntry y) { return y.Prefix.Length.CompareTo(x.Prefix.Length); });
        }

        /// <summary>Removes a route added with <see cref="AddRoute"/>.</summary>
        public void RemoveRoute(string prefix) {
            _routes.RemoveAll(r => r.Prefix == prefix);
        }

        /// <summary>Serves files from <paramref name="dir"/> under URL <paramref name="prefix"/>, e.g.
        /// <c>ServeDirectory("/assets/", "user://assets")</c>. Paths are traversal-safe, and symlinks
        /// that resolve outside <paramref name="dir"/> are refused. Longest prefix wins.
        /// With <paramref name="playersOnly"/>, requests must identify a joined player via
        /// <c>?t=&lt;token&gt;</c> or the <c>pmc_token</c> cookie (see <see cref="RequirePlayer"/>);
        /// others get 403.</summary>
        public void ServeDirectory(string prefix, string dir, bool playersOnly = false) {
            string pre = prefix.StartsWith("/", StringComparison.Ordinal) ? prefix : "/" + prefix;
            if (!pre.EndsWith("/", StringComparison.Ordinal)) pre += "/";
            _mounts.RemoveAll(m => m.Prefix == pre);
            _mounts.Add(new MountEntry { Prefix = pre, Dir = dir, PlayersOnly = playersOnly });
            _mounts.Sort(delegate (MountEntry x, MountEntry y) { return y.Prefix.Length.CompareTo(x.Prefix.Length); });
        }

        /// <summary>The joined <see cref="PmcPlayer"/> behind an HTTP request, or null. The caller
        /// authenticates with the rejoin token as <c>?t=&lt;token&gt;</c> or the <c>pmc_token</c>
        /// cookie (pmc.js sets it after join).</summary>
        public PmcPlayer RequirePlayer(PmcHttpRequest req) {
            string token;
            if (!req.Query.TryGetValue("t", out token) || token == null) token = "";
            token = token.Trim();
            if (token == "") token = CookieValue(req, "pmc_token");
            if (token == "") return null;
            PmcPlayer p;
            return _byToken.TryGetValue(token, out p) ? p : null;
        }

        /// <summary>A single cookie from the request's Cookie header, or "".</summary>
        public static string CookieValue(PmcHttpRequest req, string name) {
            foreach (string part in req.Header("cookie").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)) {
                int eq = part.IndexOf('=');
                if (eq > 0 && part.Substring(0, eq).Trim() == name) {
                    return part.Substring(eq + 1).Trim();
                }
            }
            return "";
        }

        /// <summary>The /pmc/info.json payload.</summary>
        public string InfoJson() {
            return Info().ToString(Formatting.None);
        }

        private JObject Info() {
            return new JObject {
                ["name"] = "unity-phone-mass-controllers",
                ["version"] = Version,
                ["sdk"] = SdkVersion,
                ["join_url"] = JoinUrl(),
                ["code_required"] = _joinCode != "",
                ["players"] = _players.Count,
                ["max_players"] = MaxPlayers,
                ["admin"] = AdminPin != "",
            };
        }

        private PmcHttpResponse Route(PmcHttpRequest req) {
            string path = req.Path;
            if (path.StartsWith("/pmc/", StringComparison.Ordinal)) {
                switch (path) {
                    case "/pmc/healthz":
                        return PmcHttpResponse.Text("ok\n").SetHeader("Cache-Control", "no-cache");
                    case "/pmc/info.json":
                        if (!MetaEndpointAllowed(req)) {
                            return PmcHttpResponse.Error(403, "join code required");
                        }
                        return PmcHttpResponse.Json(Info());
                    case "/pmc/qr.png":
                        if (!MetaEndpointAllowed(req)) {
                            return PmcHttpResponse.Error(403, "join code required");
                        }
                        return QrResponse(req);
                }
                if (WebDir != "") {
                    return PmcStaticFiles.Serve(WebDir, path.Substring(5), req, "");
                }
                return null;
            }
            foreach (var r in _routes) {
                if (path.StartsWith(r.Prefix, StringComparison.Ordinal)) {
                    req.RoutePrefix = r.Prefix;
                    PmcHttpResponse outp;
                    try {
                        outp = r.Handler(req);
                    } catch (Exception e) {
                        return PmcHttpResponse.Error(500, "route handler threw " + e.GetType().Name);
                    }
                    if (outp != null) return outp;
                }
            }
            foreach (var m in _mounts) {
                if (path.StartsWith(m.Prefix, StringComparison.Ordinal)) {
                    if (m.PlayersOnly && RequirePlayer(req) == null) {
                        return PmcHttpResponse.Error(403, "player token required");
                    }
                    var resp = PmcStaticFiles.Serve(m.Dir, path.Substring(m.Prefix.Length), req);
                    if (resp != null) return resp;
                }
            }
            if (ControllerDir != "") {
                return PmcStaticFiles.Serve(ControllerDir, path, req);
            }
            return null;
        }

        // While a tunnel is up the host is reachable from the public internet, and info.json / qr.png
        // reveal the join URL including the join code. They answer only to loopback or to a request
        // carrying a valid ?code=.
        private bool MetaEndpointAllowed(PmcHttpRequest req) {
            if (_tunnel == null) return true;
            if (IsLoopback(req.RemoteAddress)) return true;
            string given;
            if (!req.Query.TryGetValue("code", out given) || given == null) given = "";
            given = given.Trim();
            return _joinCode != ""
                && string.Equals(given, _joinCode.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private PmcHttpResponse QrResponse(PmcHttpRequest req) {
            int px = Clamp(ParseInt(Query(req, "px", "8")), 1, 32);
            int quiet = Clamp(ParseInt(Query(req, "quiet", "4")), 0, 16);
            var provider = QrPngProvider;
            if (provider == null) {
                return PmcHttpResponse.Error(501, "QR encoder not available");
            }
            byte[] png;
            try {
                png = provider(JoinUrl(), px, quiet);
            } catch (Exception) {
                return PmcHttpResponse.Error(500, "QR encoding failed");
            }
            if (png == null) {
                return PmcHttpResponse.Error(500, "QR encoding failed");
            }
            return PmcHttpResponse.Bytes(png, "image/png").SetHeader("Cache-Control", "no-cache");
        }

        private static string Query(PmcHttpRequest req, string key, string def) {
            string v;
            return req.Query.TryGetValue(key, out v) && v != null ? v : def;
        }

        private static int Clamp(int v, int lo, int hi) {
            return v < lo ? lo : v > hi ? hi : v;
        }

        // ---------------------------------------------------------------------------------------------
        // Polling

        /// <summary>Drains queued I/O events (accepts, HTTP requests, WebSocket events), runs timers,
        /// pumps the tunnel, and raises events — all on the caller's thread. Call every frame;
        /// <paramref name="budgetMs"/> caps the drain (default <see cref="IoBudgetMsec"/>).</summary>
        public void Poll(float budgetMs = -1) {
            // Tunnel events/pumping run even while stopped: a kept tunnel must stay alive.
            DrainTunnelEvents();
            if (_tunnel != null) _tunnel.Pump();
            if (_tunnelPending != null) _tunnelPending.Pump();
            if (!_running) return;
            long t0 = PmcTime.NowUsec();
            long now = PmcTime.NowMsec();
            float budget = budgetMs < 0 ? IoBudgetMsec : budgetMs;

            DrainIoEvents(t0, now, budget);

            if (_running && now - _lastTimerMsec >= TimerMsec) {
                _lastTimerMsec = now;
                Timers(now);
                _closedPending = true;
            }
            if (_running && _closedPending) {
                _closedPending = false;
                Reap();
            }

            long dt = PmcTime.NowUsec() - t0;
            _lastPollUsec = dt;
            if (dt > _maxPollUsec) _maxPollUsec = dt;
        }

        private void StartIoThread() {
            _io = new PmcIoWorker {
                Server = _server,
                MaxHeaderBytes = MaxHeaderBytes,
                MaxBodyBytes = MaxBodyBytes,
                MaxMessageBytes = MaxMessageBytes,
            };
            _ioThread = new Thread(_io.Run) { IsBackground = true, Name = "PmcIo" };
            _ioThread.Start();
        }

        private void StopIoThread() {
            if (_io == null) return;
            lock (_io.Mutex) {
                _io.Stop = true;
            }
            _ioThread.Join();
            _ioThread = null;
            _io = null;
        }

        // Applies complete events produced by the I/O worker. Undrained events (budget) go back to
        // the front of the worker's queue, preserving per-connection order.
        private void DrainIoEvents(long t0, long now, float budgetMs) {
            List<PmcIoEvent> evs;
            lock (_io.Mutex) {
                evs = new List<PmcIoEvent>(_io.Events);
                _io.Events.Clear();
                _bytesIn += _io.BytesIn;
                _bytesOut += _io.BytesOut;
                _io.BytesIn = 0;
                _io.BytesOut = 0;
            }
            _closedPending = true;
            long deadline = t0 + (long)(budgetMs * 1000.0);
            int i = 0;
            while (i < evs.Count && _running) {
                var e = evs[i];
                switch (e.Kind) {
                    case PmcIoEvent.KindAccept:
                        IoAccept(e.Peer, now);
                        break;
                    case PmcIoEvent.KindHttp:
                        HandleHttpRequest(e.Conn, e.Request, now);
                        e.Conn.HttpBusy = false; // response is queued — worker may extract the next request
                        break;
                    case PmcIoEvent.KindHttpError:
                        Respond(e.Conn, null, PmcHttpResponse.Error(e.Status, e.Reason), false);
                        e.Conn.HttpBusy = false;
                        break;
                    case PmcIoEvent.KindWs:
                        OnWsEvent(e.Conn, e.Ev, now);
                        break;
                }
                i += 1;
                if ((i & 15) == 0 && PmcTime.NowUsec() > deadline) {
                    lock (_io.Mutex) {
                        _io.Events.InsertRange(0, evs.GetRange(i, evs.Count - i));
                    }
                    return;
                }
            }
        }

        private void IoAccept(Socket peer, long now) {
            if (MaxConnections > 0 && _conns.Count >= MaxConnections) {
                try { peer.Close(); } catch (Exception) { }
                _refused += 1;
                return;
            }
            var conn = new PmcConnection(peer, now);
            conn.Slot = (int)(_accepted & 0x7FFFFFFF);
            // Behind the tunnel every peer is loopback; those are counted per CF-Connecting-IP on
            // their first request.
            if (!BehindTunnel(conn)) {
                if (!CountAddress(conn, conn.RemoteAddress)) {
                    conn.CloseNow("too many connections from address");
                    _refused += 1;
                    return;
                }
            }
            _conns.Add(conn);
            _accepted += 1;
            _io.AddConn(conn);
        }

        // Drops the socket on the worker's next pass.
        private void ConnClose(PmcConnection c, string reason) {
            c.RequestClose(reason);
        }

        // Applies one complete HTTP request.
        private void HandleHttpRequest(PmcConnection c, PmcHttpRequest req, long now) {
            if (!c.IsOpen() || c.Mode != PmcConnection.ConnMode.Http || c.CloseAfterFlush || c.UpgradePending) {
                return;
            }
            _httpRequests += 1;
            c.HeadStartedMsec = now;
            if (c.CountedAddress == "" && BehindTunnel(c)) {
                string addr = ForwardedAddress(c, req);
                if (!CountAddress(c, addr)) {
                    Respond(c, req, PmcHttpResponse.Error(429, "too many connections from your address"), false);
                    return;
                }
            }
            req.RemoteAddress = c.ClientAddress;
            if (req.Path == "/pmc/ws") {
                Upgrade(c, req, now);
                return;
            }
            var resp = Route(req);
            if (resp == null) {
                if (req.Method == "GET" || req.Method == "HEAD") {
                    resp = PmcHttpResponse.Error(404);
                } else {
                    resp = PmcHttpResponse.Error(405).SetHeader("Allow", "GET, HEAD");
                }
            }
            Respond(c, req, resp, req.WantsKeepAlive());
        }

        private void Respond(PmcConnection c, PmcHttpRequest req, PmcHttpResponse resp, bool keepAlive) {
            bool headOnly = req != null && req.Method == "HEAD";
            if (resp.FilePath != "") {
                FileStream f;
                try {
                    f = new FileStream(resp.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                } catch (Exception) {
                    resp = PmcHttpResponse.Error(500, "cannot open file");
                    f = null;
                }
                if (f != null) {
                    long size = f.Length;
                    long offset = Clamp64(resp.FileOffset, 0, size);
                    long length = resp.FileLength < 0 ? size - offset : Math.Min(resp.FileLength, size - offset);
                    c.Queue(resp.BuildHead(length, keepAlive));
                    if (headOnly || length <= 0) {
                        f.Dispose();
                    } else {
                        f.Seek(offset, SeekOrigin.Begin);
                        c.StartFile(f, length);
                    }
                    if (!keepAlive) c.CloseWhenFlushed();
                    return;
                }
            }
            c.Queue(resp.BuildHead(resp.Body.Length, keepAlive));
            if (!headOnly) c.Queue(resp.Body);
            if (!keepAlive) c.CloseWhenFlushed();
        }

        private static long Clamp64(long v, long lo, long hi) {
            return v < lo ? lo : v > hi ? hi : v;
        }

        private void Upgrade(PmcConnection c, PmcHttpRequest req, long now) {
            if (req.Method != "GET") {
                Respond(c, req, PmcHttpResponse.Error(405).SetHeader("Allow", "GET"), false);
                return;
            }
            if (!req.HeaderHasToken("upgrade", "websocket") || !req.HeaderHasToken("connection", "upgrade")) {
                Respond(c, req, PmcHttpResponse.Error(426, "WebSocket upgrade required").SetHeader("Upgrade", "websocket"), false);
                return;
            }
            if (req.Header("sec-websocket-version").Trim() != "13") {
                Respond(c, req, PmcHttpResponse.Error(426, "unsupported WebSocket version").SetHeader("Sec-WebSocket-Version", "13"), false);
                return;
            }
            string key = req.Header("sec-websocket-key").Trim();
            bool keyOk = key.Length == 24 && key.EndsWith("==", StringComparison.Ordinal);
            if (keyOk) {
                try {
                    keyOk = Convert.FromBase64String(key).Length == 16;
                } catch (Exception) {
                    keyOk = false;
                }
            }
            if (!keyOk) {
                Respond(c, req, PmcHttpResponse.Error(400, "bad Sec-WebSocket-Key"), false);
                return;
            }
            if (CheckOrigin) {
                string origin = req.Header("origin").Trim();
                if (origin != "" && !AllowedOrigins.Contains(origin)) {
                    Respond(c, req, PmcHttpResponse.Error(403, "origin not allowed"), false);
                    return;
                }
            }
            var resp = new PmcHttpResponse();
            resp.Status = 101;
            resp.Headers["Upgrade"] = "websocket";
            resp.Headers["Connection"] = "Upgrade";
            resp.Headers["Sec-WebSocket-Accept"] = PmcWsFrame.AcceptKey(key);
            c.Queue(resp.BuildHead(0, true));
            c.RequestUpgrade(MaxMessageBytes, now, (int)(HelloTimeoutSeconds * 1000.0), HeartbeatMsec());
        }

        private long HeartbeatMsec() {
            return HeartbeatSeconds > 0f ? (long)(HeartbeatSeconds * 1000.0) : 1L << 62;
        }

        // Applies one decoded WS event.
        private void OnWsEvent(PmcConnection c, PmcWsEvent ev, long now) {
            if (!c.IsOpen()) {
                return; // a stale event: the socket closed after the worker queued it
            }
            c.PingsUnanswered = 0;
            switch (ev.Type) {
                case PmcWsEvent.EvText:
                    if (!c.CloseSent && !c.Rejected) {
                        _msgsIn += 1;
                        OnWsText(c, ev.Text, now);
                    }
                    break;
                case PmcWsEvent.EvBinary:
                    if (!c.CloseSent && !c.Rejected) {
                        _msgsIn += 1;
                        OnWsBinary(c, ev.Payload, now);
                    }
                    break;
                case PmcWsEvent.EvPing:
                    if (!c.CloseSent) {
                        c.Queue(PmcWsFrame.Pong(ev.Payload));
                    }
                    break;
                case PmcWsEvent.EvPong:
                    if (c.PingSentMsec > 0) {
                        PmcPlayer pp;
                        if (_players.TryGetValue(c.PlayerId, out pp) && pp != null) {
                            float sample = now - c.PingSentMsec;
                            pp.RttMs = pp.RttMs <= 0f ? sample : pp.RttMs * 0.75f + sample * 0.25f;
                        }
                        c.PingSentMsec = 0;
                    }
                    break;
                case PmcWsEvent.EvClose:
                    OnPeerSocketClosing(c);
                    if (!c.CloseSent) {
                        c.Queue(PmcWsFrame.Close(ev.Code == 1005 ? 0 : ev.Code));
                        c.CloseSent = true;
                    }
                    c.CloseWhenFlushed("closed by peer");
                    if (!c.HasPendingOutput()) {
                        ConnClose(c, "closed by peer");
                    }
                    break;
                case PmcWsEvent.EvError:
                    // Protocol failure: send the close frame and keep draining input until the peer
                    // closes or the handshake timeout passes. Closing with unread input would send a
                    // TCP RST, which can destroy the close frame before the peer reads it.
                    OnPeerSocketClosing(c);
                    WsClose(c, ev.Code, ev.Reason);
                    break;
            }
        }

        // Detaches the player when the socket is going away, so grace starts immediately.
        private void OnPeerSocketClosing(PmcConnection c) {
            if (c.PlayerId != 0) {
                PmcPlayer p;
                _players.TryGetValue(c.PlayerId, out p);
                Detach(c);
                if (p != null) {
                    PlayerSocketLost(p);
                }
            }
        }

        private void WsClose(PmcConnection c, int code, string reason = "") {
            if (!c.IsOpen() || c.Mode != PmcConnection.ConnMode.Ws) return;
            if (!c.CloseSent) {
                c.Queue(PmcWsFrame.Close(code, reason));
                c.CloseSent = true;
                c.CloseDeadlineMsec = PmcTime.NowMsec() + CloseHandshakeMsec;
            }
        }

        private void SendJson(PmcConnection c, JObject obj) {
            if (c.IsOpen() && !c.CloseSent) {
                c.Queue(PmcWsFrame.Text(obj.ToString(Formatting.None)));
            }
        }

        private static byte[] EncodeMsg(object data) {
            var bytes = data as byte[];
            if (bytes != null) {
                return PmcWsFrame.Binary(bytes);
            }
            var msg = new JObject { ["t"] = "msg", ["d"] = ToJToken(data) };
            return PmcWsFrame.Text(msg.ToString(Formatting.None));
        }

        private static JToken ToJToken(object data) {
            if (data == null) return JValue.CreateNull();
            var t = data as JToken;
            if (t != null) return t;
            try {
                return JToken.FromObject(data);
            } catch (Exception) {
                return new JValue(data.ToString());
            }
        }

        // ---- timers ----

        private void Timers(long now) {
            long headerMs = (long)(HeaderTimeoutSeconds * 1000.0);
            // Indexed: event handlers may Stop() the host, which clears _conns mid-iteration.
            for (int ci = 0; ci < _conns.Count; ci++) {
                var c = _conns[ci];
                if (!c.IsOpen()) continue;
                if (c.HasPendingOutput() && now - c.LastTxMsec() > StallMsec) {
                    ConnClose(c, "write stalled");
                    OnPeerSocketClosing(c);
                    continue;
                }
                if (c.Mode == PmcConnection.ConnMode.Http) {
                    if (c.HasPendingOutput()) {
                        c.HeadStartedMsec = now;
                    } else if (now - c.HeadStartedMsec > headerMs) {
                        if (c.IoPartialIn) {
                            Respond(c, null, PmcHttpResponse.Error(408), false);
                            c.CloseWhenFlushed("header timeout");
                        } else {
                            ConnClose(c, "header timeout");
                        }
                    }
                } else if (c.Mode == PmcConnection.ConnMode.Ws) {
                    if (c.CloseSent) {
                        if (c.CloseDeadlineMsec > 0 && now >= c.CloseDeadlineMsec) {
                            ConnClose(c, "close handshake timeout");
                        }
                        continue;
                    }
                    if (c.OutPending() > MaxBacklogBytes) {
                        ConnClose(c, "backlog");
                        OnPeerSocketClosing(c);
                        continue;
                    }
                    if (c.PlayerId == 0 && !c.Rejected && now >= c.HelloDeadlineMsec) {
                        Reject(c, "bad_hello", "no pmc.hello received");
                        continue;
                    }
                    if (now >= c.NextPingMsec) {
                        if (c.PingsUnanswered >= 2) {
                            ConnClose(c, "heartbeat timeout");
                            OnPeerSocketClosing(c);
                            continue;
                        }
                        c.Queue(PmcWsFrame.Ping());
                        c.PingsUnanswered += 1;
                        c.PingSentMsec = now;
                        c.NextPingMsec = now + HeartbeatMsec();
                    }
                }
            }

            if (_hintDeadlineMsec > 0 && !_hintFired && now >= _hintDeadlineMsec) {
                _hintFired = true;
                if (_joinCount == _hintJoinsAtArm) {
                    NoJoinsHint?.Invoke();
                }
            }

            if (_autoRestartAtMsec > 0 && now >= _autoRestartAtMsec) {
                _autoRestartAtMsec = 0;
                if (_autoRestartEpoch == _tunnelEpoch && _tunnel == null && _tunnelPending == null && _running) {
                    StartTunnel();
                    _autoRestarts = _autoRestartCount;
                }
            }

            if (now - _lastSweepMsec >= 100) {
                _lastSweepMsec = now;
                var timedOut = new List<PmcPlayer>();
                foreach (var p in _players.Values) {
                    if (!p.Connected && p.GraceDeadlineMs > 0 && now >= p.GraceDeadlineMs) {
                        timedOut.Add(p);
                    }
                }
                foreach (var p in timedOut) {
                    RemovePlayer(p, "timeout");
                    if (!_running) return;
                }
                var expired = new List<string>();
                foreach (var kv in _tombstones) {
                    if (now >= kv.Value.ExpiresMsec) expired.Add(kv.Key);
                }
                foreach (var k in expired) _tombstones.Remove(k);
                var gone = new List<string>();
                foreach (var kv in _addrFailures) {
                    var e = kv.Value;
                    if (now >= e.WindowEndMsec && now >= e.BlockedUntilMsec) gone.Add(kv.Key);
                }
                foreach (var k in gone) _addrFailures.Remove(k);
            }
        }

        private void Reap() {
            // Snapshot: socket-lost handlers may mutate the player's state (and even Stop the host,
            // which clears _conns), so the list itself is only pruned after the callbacks run.
            var all = _conns.ToArray();
            var closed = new List<PmcConnection>();
            foreach (var c in all) {
                if (!c.IsOpen()) closed.Add(c);
            }
            if (closed.Count == 0) return;
            foreach (var c in closed) {
                UncountAddress(c);
                OnPeerSocketClosing(c);
            }
            _conns.RemoveAll(c => !c.IsOpen());
        }

        // ---------------------------------------------------------------------------------------------
        // Per-address limits

        // True when the socket comes from the local tunnel process, so the real client is in
        // CF-Connecting-IP.
        private bool BehindTunnel(PmcConnection c) {
            return _tunnel != null && IsLoopback(c.RemoteAddress);
        }

        private static bool IsLoopback(string addr) {
            return addr != null
                && (addr.StartsWith("127.", StringComparison.Ordinal)
                    || addr == "::1" || addr == "::ffff:127.0.0.1");
        }

        private static string ForwardedAddress(PmcConnection c, PmcHttpRequest req) {
            string h = req.Header("cf-connecting-ip").Trim();
            IPAddress ip;
            if (h != "" && IPAddress.TryParse(h, out ip)) {
                return h;
            }
            return c.RemoteAddress;
        }

        private bool CountAddress(PmcConnection c, string addr) {
            if (MaxConnectionsPerAddress > 0) {
                int cur;
                if (_addrConns.TryGetValue(addr, out cur) && cur >= MaxConnectionsPerAddress) {
                    return false;
                }
            }
            _addrConns[addr] = (_addrConns.TryGetValue(addr, out int n) ? n : 0) + 1;
            c.CountedAddress = addr;
            c.ClientAddress = addr;
            return true;
        }

        private void UncountAddress(PmcConnection c) {
            if (c.CountedAddress == "") return;
            int n;
            if (_addrConns.TryGetValue(c.CountedAddress, out n)) {
                n -= 1;
                if (n <= 0) _addrConns.Remove(c.CountedAddress);
                else _addrConns[c.CountedAddress] = n;
            }
            c.CountedAddress = "";
        }

        // Milliseconds addr is still blocked for kind, or 0.
        private long BlockedMs(string kind, string addr, long now) {
            AddrFailure e;
            if (!_addrFailures.TryGetValue(kind + "|" + addr, out e)) return 0;
            return Math.Max(0, e.BlockedUntilMsec - now);
        }

        // Records a failure. Returns true if this failure triggered a block.
        private bool RecordFailure(string kind, string addr, long now, int maxFailures, long blockMsec) {
            if (maxFailures <= 0) return false;
            string key = kind + "|" + addr;
            AddrFailure e;
            if (!_addrFailures.TryGetValue(key, out e) || e == null || now >= e.WindowEndMsec) {
                long blocked = e != null ? e.BlockedUntilMsec : 0;
                e = new AddrFailure { Count = 0, WindowEndMsec = now + blockMsec, BlockedUntilMsec = blocked };
            }
            e.Count += 1;
            bool blockedNow = false;
            if (e.Count >= maxFailures) {
                e.BlockedUntilMsec = now + blockMsec;
                e.Count = 0;
                e.WindowEndMsec = now + blockMsec;
                blockedNow = true;
            }
            _addrFailures[key] = e;
            return blockedNow;
        }

        // ---------------------------------------------------------------------------------------------
        // Protocol

        private void OnWsBinary(PmcConnection c, byte[] data, long now) {
            if (c.PlayerId == 0) {
                Reject(c, "bad_hello", "first frame must be pmc.hello");
                return;
            }
            PmcPlayer p;
            if (!_players.TryGetValue(c.PlayerId, out p) || p == null) return;
            p.LastSeenMs = now;
            MessageReceived?.Invoke(p, data);
        }

        private void OnWsText(PmcConnection c, string text, long now) {
            JObject m = null;
            try {
                var parsed = JToken.Parse(text);
                m = parsed as JObject;
            } catch (Exception) {
                m = null;
            }
            var tTok = m != null ? m["t"] : null;
            if (m == null || tTok == null || tTok.Type != JTokenType.String) {
                if (c.PlayerId == 0) {
                    Reject(c, "bad_hello", "first frame must be pmc.hello");
                }
                return;
            }
            string t = (string)tTok;
            if (c.PlayerId == 0) {
                if (t != "pmc.hello") {
                    Reject(c, "bad_hello", "first frame must be pmc.hello");
                    return;
                }
                OnHello(c, m, now);
                return;
            }
            PmcPlayer p;
            if (!_players.TryGetValue(c.PlayerId, out p) || p == null) return;
            p.LastSeenMs = now;
            switch (t) {
                case "msg":
                    MessageReceived?.Invoke(p, m["d"]);
                    break;
                case "pmc.ping":
                    SendJson(c, new JObject {
                        ["t"] = "pmc.pong",
                        ["c"] = m["c"] ?? JValue.CreateNull(),
                        ["s"] = PmcTime.EpochMs(),
                    });
                    break;
                case "pmc.profile":
                    if (ApplyIdentity(p, m)) {
                        PlayerUpdated?.Invoke(p);
                    }
                    break;
                case "pmc.auth":
                    OnAuth(c, p, m, now);
                    break;
                case "pmc.leave":
                    Detach(c);
                    WsClose(c, 1000, "leave");
                    RemovePlayer(p, "leave");
                    break;
            }
        }

        private void OnHello(PmcConnection c, JObject m, long now) {
            var sdk = m["sdk"];
            if (sdk == null || (sdk.Type != JTokenType.Integer && sdk.Type != JTokenType.Float)) {
                Reject(c, "bad_hello", "missing sdk version");
                return;
            }
            if ((int)sdk != SdkVersion) {
                Reject(c, "version", "host speaks sdk " + SdkVersion);
                return;
            }
            string token = "";
            var tokenTok = m["token"];
            if (tokenTok != null && tokenTok.Type == JTokenType.String) {
                token = (string)tokenTok;
            }
            if (token != "" && _banned.Contains(token)) {
                Reject(c, "banned", "you were removed from this game");
                return;
            }

            PmcPlayer existing = token != "" ? (_byToken.TryGetValue(token, out var ex) ? ex : null) : null;
            if (existing != null) {
                if (existing.Conn != null && !ReferenceEquals(existing.Conn, c)) {
                    var old = existing.Conn;
                    Detach(old);
                    SendJson(old, new JObject { ["t"] = "pmc.replaced" });
                    WsClose(old, 4002, "replaced");
                }
                Attach(c, existing, now);
                bool changed = ApplyIdentity(existing, m);
                Welcome(c, existing, true);
                _joinCount += 1;
                PlayerRejoined?.Invoke(existing);
                if (changed && ReferenceEquals(existing.Conn, c)) {
                    PlayerUpdated?.Invoke(existing);
                }
                return;
            }

            Tombstone tomb = null;
            if (token != "") _tombstones.TryGetValue(token, out tomb);
            if (_joinCode != "" && tomb == null) {
                long waitMs = BlockedMs("code", c.ClientAddress, now);
                if (waitMs > 0) {
                    Reject(c, "bad_code", "too many wrong join codes; try again in "
                        + (long)Math.Ceiling(waitMs / 1000.0) + " s");
                    return;
                }
                var codeTok = m["code"];
                string given = codeTok != null && codeTok.Type == JTokenType.String ? ((string)codeTok).Trim() : "";
                if (!string.Equals(given, _joinCode.Trim(), StringComparison.OrdinalIgnoreCase)) {
                    // Only non-empty wrong guesses count toward the per-address block (a missing code
                    // isn't a guess).
                    if (given != "") {
                        RecordFailure("code", c.ClientAddress, now, JoinCodeMaxFailures,
                            (long)(JoinCodeBlockSeconds * 1000.0));
                    }
                    Reject(c, "bad_code", "wrong or missing join code");
                    return;
                }
            }
            if (MaxPlayers > 0 && _players.Count >= MaxPlayers) {
                Reject(c, "full", "the game is full");
                return;
            }

            var p = new PmcPlayer();
            bool rejoined = false;
            if (tomb != null) {
                _tombstones.Remove(token);
                p.Id = tomb.Id;
                p.Token = token;
                p.Name = tomb.Name;
                p.Profile = tomb.Profile;
                p.Meta = tomb.Meta;
                rejoined = true;
            } else {
                p.Id = _nextId;
                _nextId += 1;
                p.Token = GenerateToken();
            }
            ApplyIdentity(p, m);
            if (p.Name == "") {
                p.Name = "Player " + p.Id;
            }
            p.JoinedMs = now;
            _players[p.Id] = p;
            _byToken[p.Token] = p;
            Attach(c, p, now);
            Welcome(c, p, rejoined);
            _joinCount += 1;
            PlayerJoined?.Invoke(p);
        }

        private static string GenerateToken() {
            var b = new byte[16];
            using (var rng = RandomNumberGenerator.Create()) {
                rng.GetBytes(b);
            }
            var sb = new StringBuilder(32);
            foreach (byte x in b) sb.Append(x.ToString("x2"));
            return sb.ToString();
        }

        private void Attach(PmcConnection c, PmcPlayer p, long now) {
            c.PlayerId = p.Id;
            p.Conn = c;
            p.Connected = true;
            p.GraceDeadlineMs = 0;
            p.LastSeenMs = now;
            p.RemoteAddress = c.ClientAddress;
        }

        private void Detach(PmcConnection c) {
            if (c.PlayerId == 0) return;
            PmcPlayer p;
            _players.TryGetValue(c.PlayerId, out p);
            c.PlayerId = 0;
            if (p != null && ReferenceEquals(p.Conn, c)) {
                p.Conn = null;
            }
        }

        private void PlayerSocketLost(PmcPlayer p) {
            if (!_players.ContainsKey(p.Id) || p.Conn != null || !p.Connected) return;
            long now = PmcTime.NowMsec();
            p.Connected = false;
            p.LastSeenMs = now;
            p.GraceDeadlineMs = now + Math.Max(1, (long)(GraceSeconds * 1000.0));
            PlayerDisconnected?.Invoke(p);
            PmcPlayer cur;
            if (GraceSeconds <= 0f && _players.TryGetValue(p.Id, out cur) && ReferenceEquals(cur, p) && !p.Connected) {
                RemovePlayer(p, "timeout");
            }
        }

        private void RemovePlayer(PmcPlayer p, string reason, bool tombstone = false) {
            PmcPlayer cur;
            if (!_players.TryGetValue(p.Id, out cur) || !ReferenceEquals(cur, p)) return;
            _players.Remove(p.Id);
            _byToken.Remove(p.Token);
            if (p.Conn != null) {
                var c = p.Conn;
                Detach(c);
                WsClose(c, 1000, reason);
            }
            p.Connected = false;
            p.GraceDeadlineMs = 0;
            if ((reason == "timeout" || tombstone) && RememberSeconds > 0f) {
                _tombstones[p.Token] = new Tombstone {
                    Id = p.Id,
                    Name = p.Name,
                    Profile = p.Profile,
                    Meta = p.Meta,
                    ExpiresMsec = PmcTime.NowMsec() + (long)(RememberSeconds * 1000.0),
                };
            }
            PlayerLeft?.Invoke(p, reason);
        }

        private bool ApplyIdentity(PmcPlayer p, JObject m) {
            bool changed = false;
            var nameTok = m["name"];
            if (nameTok != null && nameTok.Type == JTokenType.String) {
                string nm = CleanName((string)nameTok);
                if (nm != "" && nm != p.Name) {
                    p.Name = nm;
                    changed = true;
                }
            }
            var profTok = m["profile"];
            if (profTok != null && profTok.Type == JTokenType.Object && !JToken.DeepEquals(profTok, p.Profile)) {
                p.Profile = (JObject)profTok;
                changed = true;
            }
            return changed;
        }

        private static string CleanName(string s) {
            var sb = new StringBuilder(s.Length);
            foreach (char ch in s.Trim()) {
                if (ch >= 32 && ch != 127) sb.Append(ch);
            }
            string outp = sb.ToString().Trim();
            return outp.Length > MaxNameLength ? outp.Substring(0, MaxNameLength) : outp;
        }

        private void Welcome(PmcConnection c, PmcPlayer p, bool rejoined) {
            SendJson(c, new JObject {
                ["t"] = "pmc.welcome",
                ["id"] = p.Id,
                ["token"] = p.Token,
                ["name"] = p.Name,
                ["profile"] = p.Profile != null ? (JToken)p.Profile.DeepClone() : new JObject(),
                ["rejoined"] = rejoined,
                ["admin"] = p.IsAdmin,
                ["server_ms"] = PmcTime.EpochMs(),
                ["join_url"] = JoinUrl(),
            });
        }

        private void Reject(PmcConnection c, string code, string reason) {
            c.Rejected = true;
            SendJson(c, new JObject { ["t"] = "pmc.reject", ["code"] = code, ["reason"] = reason });
            WsClose(c, 4000, code);
        }

        private void OnAuth(PmcConnection c, PmcPlayer p, JObject m, long now) {
            // Per connection: 5 failures -> 30 s. Per address (so reconnecting doesn't reset it):
            // 20 failures -> 60 s. Global: AdminPinMaxFailures across all addresses disables the PIN
            // until restart.
            long locked = Math.Max(c.AuthLockedUntilMsec - now, BlockedMs("auth", c.ClientAddress, now));
            if (locked > 0) {
                SendJson(c, new JObject { ["t"] = "pmc.auth", ["ok"] = false, ["locked_ms"] = locked });
                return;
            }
            if (_authDisabled) {
                SendJson(c, new JObject { ["t"] = "pmc.auth", ["ok"] = false, ["disabled"] = true });
                return;
            }
            var pinTok = m["pin"];
            bool ok = AdminPin != "" && pinTok != null && pinTok.Type == JTokenType.String
                && SecureEquals((string)pinTok, AdminPin);
            if (ok) {
                c.AuthFailures = 0;
                SendJson(c, new JObject { ["t"] = "pmc.auth", ["ok"] = true });
                if (!p.IsAdmin) {
                    p.IsAdmin = true;
                    AdminAuthenticated?.Invoke(p);
                }
                return;
            }
            c.AuthFailures += 1;
            _authFailTotal += 1;
            if (c.AuthFailures >= AuthMaxFailures) {
                c.AuthFailures = 0;
                c.AuthLockedUntilMsec = now + AuthLockMsec;
            }
            RecordFailure("auth", c.ClientAddress, now, AuthAddrMaxFailures, AuthAddrLockMsec);
            if (AdminPinMaxFailures > 0 && _authFailTotal >= AdminPinMaxFailures) {
                _authDisabled = true;
            }
            SendJson(c, new JObject { ["t"] = "pmc.auth", ["ok"] = false });
        }

        private static bool SecureEquals(string a, string b) {
            byte[] x, y;
            using (var sha = SHA256.Create()) {
                x = sha.ComputeHash(Encoding.UTF8.GetBytes(a ?? ""));
                y = sha.ComputeHash(Encoding.UTF8.GetBytes(b ?? ""));
            }
            return CryptographicOperations.FixedTimeEquals(x, y);
        }

        // ---------------------------------------------------------------------------------------------
        // Tunnel

        /// <summary>Shares this host outside the LAN through a Cloudflare tunnel (starting the host
        /// first if needed). Progress is reported via <see cref="TunnelStateChanged"/>. When it's
        /// ready, <see cref="AdvertiseUrl"/> becomes the tunnel URL and a 6-letter
        /// <see cref="JoinCode"/> is generated if none is set — unless <paramref name="code"/> (or
        /// <see cref="TunnelJoinCode"/>) supplies one, which is then used as-is and never
        /// auto-cleared.
        ///
        /// Calling it while a healthy tunnel already points at the current port is a no-op (the URL
        /// is re-announced) — a tunnel kept across <see cref="Stop"/>/<see cref="Start"/> or
        /// re-adopted after a host swap is reused, not replaced. Call <see cref="RestartTunnel"/> to
        /// force a fresh URL.</summary>
        public void StartTunnel(string code = "") {
            _tunnelEpoch += 1;
            _autoRestarts = 0;
            if (_tunnel != null) {
                string st = _tunnel.State ?? "";
                if (st == "starting" || st == "downloading") {
                    return;
                }
                if (st == "ready" || st == "lost") {
                    bool samePort = _tunnelLocalPort >= 0 && _tunnelLocalPort == _port && _running;
                    if (st == "ready" && samePort) {
                        ApplyTunnelReady(_tunnel.Url ?? "");
                        return;
                    }
                    // Lost, or retargeting a kept tunnel to a new port: bring a replacement up first.
                    if (RollingRestart(code)) {
                        return;
                    }
                }
                StopTunnelInternal(true); // failed/stopped leftover, or rolling restart unavailable
            }
            if (!_running) {
                int err = Start();
                if (err != 0) {
                    TunnelStateChanged?.Invoke("failed", "host failed to start: " + err);
                    return;
                }
            }
            _pendingTunnelCode = code != "" ? code : TunnelJoinCode;
            if (AdoptDetached()) {
                return;
            }
            var t = MakeTunnel();
            _tunnel = t;
            _tunnelLocalPort = _port;
            t.StateChanged += OnTunnelStateMain;
            try {
                t.Start(_port);
            } catch (Exception) {
                // the tunnel reports through StateChanged
            }
        }

        /// <summary>Brings up a fresh tunnel while the old one keeps serving: connected players get a
        /// <c>pmc.moved</c> notice with the new join URL before the old tunnel goes down. When no
        /// live tunnel exists this is the same as <see cref="StartTunnel"/>.</summary>
        public void RestartTunnel(string code = "") {
            _tunnelEpoch += 1;
            _autoRestarts = 0;
            if (_tunnel == null) {
                StartTunnel(code);
                return;
            }
            string st = _tunnel.State ?? "";
            if (st != "ready" && st != "lost") {
                StartTunnel(code);
                return;
            }
            if (!_running) {
                int err = Start();
                if (err != 0) {
                    TunnelStateChanged?.Invoke("failed", "host failed to start: " + err);
                    return;
                }
            }
            if (!RollingRestart(code)) {
                TunnelStateChanged?.Invoke("failed", "PmcTunnel could not be created");
            }
        }

        /// <summary>Stops the tunnel and restores the previous join URL (and clears an auto-generated
        /// join code).</summary>
        public void StopTunnel() {
            _tunnelEpoch += 1;
            _autoRestarts = 0;
            CancelPending();
            if (_tunnel != null) {
                StopTunnelInternal(true);
            }
        }

        /// <summary>The active tunnel object, or null.</summary>
        public PmcTunnel GetTunnel() {
            return _tunnel;
        }

        /// <summary>Builds a PmcTunnel with the Tunnel* settings applied.</summary>
        private PmcTunnel MakeTunnel() {
            var t = new PmcTunnel();
            t.AllowDownload = TunnelAllowDownload;
            t.BinaryPath = CloudflaredPath;
            t.VerifyDns = TunnelVerifyDns;
            t.ReadyTimeoutSec = TunnelReadyTimeoutSec;
            t.ExtraArgs = TunnelExtraArgs ?? new string[0];
            t.Mode = TunnelMode;
            t.NamedToken = NamedTunnelToken;
            t.NamedHostname = NamedTunnelHostname;
            t.NamedName = NamedTunnelName;
            t.NamedCredentialsFile = NamedTunnelCredentialsFile;
            return t;
        }

        // Brings up a replacement tunnel alongside the running one; the pending-state handler swaps
        // them once it's ready.
        private bool RollingRestart(string code) {
            if (_tunnelPending != null) return true;
            _pendingTunnelCode = code != "" ? code : TunnelJoinCode;
            var t = MakeTunnel();
            _tunnelPending = t;
            _tunnelPendingLocalPort = _port;
            t.StateChanged += OnTunnelStatePending;
            try {
                t.Start(_port);
            } catch (Exception) {
                // reported through StateChanged
            }
            return true;
        }

        private void CancelPending() {
            var t = _tunnelPending;
            _tunnelPending = null;
            if (t == null) return;
            try {
                t.StateChanged -= OnTunnelStatePending;
                t.Stop();
            } catch (Exception) {
            }
        }

        // Tunnel state changes may arrive on the tunnel's own threads; they queue here and are
        // applied inside Poll().
        private void OnTunnelStateMain(string state, string detail) {
            lock (_tunnelEvents) {
                _tunnelEvents.Add(new TunnelEvent { Pending = false, State = state, Detail = detail });
            }
        }

        private void OnTunnelStatePending(string state, string detail) {
            lock (_tunnelEvents) {
                _tunnelEvents.Add(new TunnelEvent { Pending = true, State = state, Detail = detail });
            }
        }

        private void DrainTunnelEvents() {
            TunnelEvent[] evs;
            lock (_tunnelEvents) {
                if (_tunnelEvents.Count == 0) return;
                evs = _tunnelEvents.ToArray();
                _tunnelEvents.Clear();
            }
            foreach (var e in evs) {
                if (e.Pending) OnTunnelPendingState(e.State, e.Detail);
                else OnTunnelState(e.State, e.Detail);
            }
        }

        private void OnTunnelPendingState(string state, string detail) {
            var t = _tunnelPending;
            if (t == null) return;
            switch (state) {
                case "ready": {
                    _tunnelPending = null;
                    t.StateChanged -= OnTunnelStatePending;
                    t.StateChanged += OnTunnelStateMain;
                    var old = _tunnel;
                    _tunnel = t;
                    _tunnelLocalPort = _tunnelPendingLocalPort;
                    // Sends pmc.moved with the new URL before the old tunnel goes down.
                    ApplyTunnelReady(detail);
                    if (old != null) {
                        try {
                            old.StateChanged -= OnTunnelStateMain;
                            old.Stop();
                        } catch (Exception) {
                        }
                    }
                    break;
                }
                case "failed":
                    CancelPending();
                    // The old tunnel is still up; report the failed restart but keep it.
                    TunnelStateChanged?.Invoke("failed", detail);
                    break;
                case "stopped":
                    CancelPending();
                    break;
                default:
                    TunnelStateChanged?.Invoke(state, detail);
                    break;
            }
        }

        // Re-adopts a tunnel detached by a discarded host. Returns true when a running tunnel was
        // reused.
        private bool AdoptDetached() {
            DetachedTunnel found = null;
            var stale = new List<DetachedTunnel>();
            lock (Detached) {
                foreach (var d in Detached) {
                    var t = d.Tunnel;
                    bool alive = PmcTunnelSeam.IsProcessAlive(t) || t.State == "starting";
                    bool portOk = d.LocalPort == _port
                        || (PmcTunnelSeam.LocalPort(t) >= 0 && PmcTunnelSeam.LocalPort(t) == _port);
                    if (portOk && alive) {
                        found = d;
                    } else {
                        stale.Add(d); // wrong port or dead process: dispose of the orphan
                    }
                }
                if (found != null) Detached.Remove(found);
                foreach (var d in stale) Detached.Remove(d);
            }
            foreach (var d in stale) {
                PmcTunnelSeam.SetDetached(d.Tunnel, false);
                try { d.Tunnel.Stop(); } catch (Exception) { }
            }
            if (found == null) return false;
            var tun = found.Tunnel;
            PmcTunnelSeam.SetDetached(tun, false);
            _tunnel = tun;
            _tunnelLocalPort = found.LocalPort;
            tun.StateChanged += OnTunnelStateMain;
            if (tun.State == "ready") {
                ApplyTunnelReady(tun.Url ?? "");
            } else {
                TunnelStateChanged?.Invoke(tun.State ?? "", "");
            }
            return true;
        }

        private void StopTunnelInternal(bool emit) {
            var t = _tunnel;
            _tunnel = null;
            try {
                t.StateChanged -= OnTunnelStateMain;
                t.Stop();
            } catch (Exception) {
            }
            RestoreAfterTunnel(emit);
            if (emit) {
                TunnelStateChanged?.Invoke("stopped", "");
            }
        }

        // The "ready" path: advertise the tunnel URL, pick a join code and notify. When a previous
        // tunnel URL is replaced, connected players get pmc.moved with the new join URL.
        private void ApplyTunnelReady(string detail) {
            string url = _tunnel != null && !string.IsNullOrEmpty(_tunnel.Url) ? _tunnel.Url : detail;
            if (url == "") return;
            bool hadTunnel = _tunnelSetAdvertise;
            string prevJoin = hadTunnel ? JoinUrl() : "";
            _suppressUrlSignal = true;
            if (!_tunnelSetAdvertise) {
                _tunnelPrevAdvertise = _advertiseUrl;
                _tunnelSetAdvertise = true;
            }
            AdvertiseUrl = url;
            if (_pendingTunnelCode != "") {
                JoinCode = _pendingTunnelCode;
                _pendingTunnelCode = "";
                _tunnelGeneratedCode = false;
            } else if (_joinCode == "") {
                JoinCode = GenerateCode(6);
                _tunnelGeneratedCode = true;
            }
            _suppressUrlSignal = false;
            string newJoin = JoinUrl();
            TunnelStateChanged?.Invoke("ready", url);
            if (_running) {
                ArmHint();
                JoinUrlChanged?.Invoke(newJoin);
            }
            if (hadTunnel && newJoin != prevJoin) {
                BroadcastMoved(newJoin);
            }
        }

        // Tells every connected player the join URL changed (they were issued tokens on the old
        // tunnel URL).
        private void BroadcastMoved(string url) {
            var msg = new JObject { ["t"] = "pmc.moved", ["d"] = new JObject { ["url"] = url } };
            byte[] frame = PmcWsFrame.Text(msg.ToString(Formatting.None));
            foreach (var p in _players.Values) {
                var c = p.Conn;
                if (c != null && !c.CloseSent && c.IsOpen()) {
                    c.Queue(frame);
                    _msgsOut += 1;
                }
            }
        }

        private void OnTunnelState(string state, string detail) {
            switch (state) {
                case "ready":
                    _autoRestarts = 0;
                    ApplyTunnelReady(detail);
                    break;
                case "lost": {
                    bool alive = _tunnel != null && PmcTunnelSeam.IsProcessAlive(_tunnel);
                    if (alive) {
                        // The process lives and may re-register; keep the tunnel URL advertised.
                        TunnelStateChanged?.Invoke("lost", detail);
                        return;
                    }
                    if (_tunnel != null) {
                        var t = _tunnel;
                        _tunnel = null;
                        try { t.StateChanged -= OnTunnelStateMain; } catch (Exception) { }
                    }
                    RestoreAfterTunnel(true);
                    TunnelStateChanged?.Invoke("lost", detail);
                    MaybeAutoRestart();
                    break;
                }
                case "failed":
                case "stopped": {
                    if (_tunnel != null) {
                        var t = _tunnel;
                        _tunnel = null;
                        try { t.StateChanged -= OnTunnelStateMain; } catch (Exception) { }
                    }
                    RestoreAfterTunnel(true);
                    TunnelStateChanged?.Invoke(state, detail);
                    break;
                }
                default:
                    TunnelStateChanged?.Invoke(state, detail);
                    break;
            }
        }

        // Restarts a tunnel that dropped after being ready. Bounded to MaxAutoRestarts per
        // StartTunnel; a "failed" tunnel never auto-restarts.
        private void MaybeAutoRestart() {
            if (!TunnelAutoRestart || !_running || _autoRestarts >= MaxAutoRestarts) return;
            _autoRestarts += 1;
            _autoRestartEpoch = _tunnelEpoch;
            _autoRestartCount = _autoRestarts; // StartTunnel resets the counter; restore it so the bound holds
            _autoRestartAtMsec = PmcTime.NowMsec() + (long)(Math.Max(TunnelRestartDelaySec, 0f) * 1000.0);
        }

        private void RestoreAfterTunnel(bool emit) {
            bool changed = false;
            _suppressUrlSignal = true;
            if (_tunnelSetAdvertise) {
                AdvertiseUrl = _tunnelPrevAdvertise;
                _tunnelSetAdvertise = false;
                changed = true;
            }
            if (_tunnelGeneratedCode) {
                JoinCode = "";
                _tunnelGeneratedCode = false;
                changed = true;
            }
            _suppressUrlSignal = false;
            if (changed && emit && _running) {
                JoinUrlChanged?.Invoke(JoinUrl());
            }
        }

        private static string GenerateCode(int length) {
            var b = new byte[length];
            using (var rng = RandomNumberGenerator.Create()) {
                rng.GetBytes(b);
            }
            var sb = new StringBuilder(length);
            foreach (byte x in b) {
                sb.Append(CodeAlphabet[x % CodeAlphabet.Length]);
            }
            return sb.ToString();
        }

        private void UrlChanged() {
            if (_running && !_suppressUrlSignal) {
                ArmHint();
                JoinUrlChanged?.Invoke(JoinUrl());
            }
        }

        private void ArmHint() {
            if (NoJoinsHintSeconds <= 0f) return;
            _hintDeadlineMsec = PmcTime.NowMsec() + (long)(NoJoinsHintSeconds * 1000.0);
            _hintJoinsAtArm = _joinCount;
            _hintFired = false;
        }
    }
}
