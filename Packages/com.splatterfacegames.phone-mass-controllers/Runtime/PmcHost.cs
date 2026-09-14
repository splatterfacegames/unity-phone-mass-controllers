using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Splatter.Pmc
{
    /// <summary>
    /// Hosts phone controllers: an HTTP/1.1 + WebSocket server on one TCP port, plus player sessions.
    ///
    /// Add this component to a GameObject, point <see cref="ControllerDir"/> at your controller page,
    /// and enable <see cref="Autostart"/> (or call <see cref="StartHost"/>). Phones open
    /// <see cref="JoinUrl"/> — show <see cref="QrTexture"/> on a lobby canvas — load the page, and
    /// connect with <c>pmc.js</c>. Game messages arrive via <see cref="MessageReceived"/>; reply with
    /// <see cref="PmcHostCore.Send"/> / <see cref="PmcHostCore.Broadcast"/>.
    ///
    /// The serialized fields mirror every <see cref="PmcHostCore"/> setting and are pushed into
    /// <see cref="Host"/> when it is created (lazily on first access, or in Awake). Change them in
    /// the Inspector, or set them on <see cref="Host"/> before Start — <see cref="ApplySettings"/>
    /// re-pushes the Inspector values.
    ///
    /// Everything runs on the main thread: <see cref="Update"/> calls <see cref="PmcHostCore.Poll"/>,
    /// which drains the socket I/O thread's event queue, so all events fire inside Poll. In the
    /// Editor the component also ticks in edit mode (via EditorApplication.update), so hosts can run
    /// without entering Play mode — the Phone Controllers dock uses <see cref="PmcLiveHosts.All"/>
    /// to find them.
    /// </summary>
    [ExecuteAlways]
    [AddComponentMenu("Splatter/Phone Mass Controllers")]
    public sealed class PmcHost : MonoBehaviour
    {
        // ------------------------------------------------------------------ settings (Inspector)
        // Every field here mirrors a PmcHostCore setting; ApplySettings() pushes all of them.

        [Header("Server")]
        [Tooltip("Preferred TCP port. 0 picks a free ephemeral port.")]
        [SerializeField] private int port = 8080;
        [Tooltip("If Port is busy, try up to this many ports above it.")]
        [SerializeField] private int portSearch = 20;
        [Tooltip("Interface to bind: \"*\" for all interfaces, or a specific IPv4 address.")]
        [SerializeField] private string bindAddress = "*";
        [Tooltip("Directory served at \"/\" (index.html by default). Resolution rules:\n" +
            "• project-relative (\"Assets/...\" or \"Packages/...\") — in the Editor, resolved against the project folder; " +
            "in a player build, resolved to \"StreamingAssets/pmc/<folder name>\" (the build preprocessor copies it there).\n" +
            "• \"StreamingAssets/...\" — resolved under Application.streamingAssetsPath.\n" +
            "• an absolute path — used as-is.")]
        [SerializeField] private string controllerDir = "";
        [Tooltip("When non-empty, new players must send this code (case-insensitive). " +
            "It is included in the join URL as ?code=. While a tunnel is up a code is strongly recommended.")]
        [SerializeField] private string joinCode = "";
        [Tooltip("Maximum players, including those in their grace period. 0 = unlimited.")]
        [SerializeField] private int maxPlayers = 0;
        [Tooltip("Seconds a disconnected player keeps their slot before PlayerLeft fires with \"timeout\". " +
            "60–120 is a good value for pocketed phones.")]
        [SerializeField] private float graceSeconds = 30f;
        [Tooltip("Seconds a timed-out player's token is remembered, so the same id and meta come back on reconnect.")]
        [SerializeField] private float rememberSeconds = 3600f;
        [Tooltip("WebSocket ping interval in seconds. A socket is closed after 2 intervals with no inbound frame. 0 disables it.")]
        [SerializeField] private float heartbeatSeconds = 15f;
        [Tooltip("Largest accepted WebSocket message in bytes (after reassembly).")]
        [SerializeField] private int maxMessageBytes = 1 << 20;
        [Tooltip("PIN for pmc.auth admin elevation. Empty means admin auth always fails.")]
        [SerializeField] private string adminPin = "";
        [Tooltip("Overrides the join URL base (e.g. \"https://example.com/\"). " +
            "Empty uses the best LAN IPv4 (or the tunnel URL once it is ready).")]
        [SerializeField] private string advertiseUrl = "";
        [Tooltip("Call Start() in Unity's Start(). When off, call StartHost() yourself.")]
        [SerializeField] private bool autostart = false;
        [Tooltip("When > 0, NoJoinsHint fires once if no player has joined this many seconds after the join URL went up " +
            "(start, or a later URL change such as the tunnel coming up). Guest Wi-Fi often isolates clients — " +
            "use it to suggest the tunnel or a hotspot. 0 = off.")]
        [SerializeField] private float noJoinsHintSeconds = 0f;

        [Header("Limits")]
        [Tooltip("Poll the sockets automatically from Update() (and the edit-mode pump). " +
            "Turn it off to call Poll() yourself.")]
        [SerializeField] private bool autoPoll = true;
        [Tooltip("Maximum milliseconds spent draining queued I/O events per Poll(). The remainder continues next frame.")]
        [SerializeField] private float ioBudgetMsec = 8f;
        [Tooltip("Maximum simultaneous TCP connections. Extra ones are closed on accept. 0 = unlimited.")]
        [SerializeField] private int maxConnections = 0;
        [Tooltip("Maximum simultaneous connections from one client address. Behind a tunnel the address comes from " +
            "CF-Connecting-IP, so players sharing a venue's public IP count together. 0 = unlimited.")]
        [SerializeField] private int maxConnectionsPerAddress = 8;
        [Tooltip("Wrong join codes an address may send before it is blocked for 60 seconds.")]
        [SerializeField] private int joinCodeMaxFailures = 10;
        [Tooltip("Wrong admin PIN attempts across the whole host before the PIN is disabled until restart (0 = unlimited). " +
            "Sits on top of the per-connection (5 → 30 s) and per-address (20 → 60 s) lockouts.")]
        [SerializeField] private int adminPinMaxFailures = 20;
        [Tooltip("When on, a WebSocket upgrade with an Origin header not in AllowedOrigins is refused with 403. " +
            "Auth is by token and join code, so the risk without it is low — enable it on a tunneled host to stop " +
            "arbitrary websites driving the socket from a visitor's browser. Clients that send no Origin are not affected.")]
        [SerializeField] private bool checkOrigin = false;
        [Tooltip("Origin header values allowed to open the WebSocket when CheckOrigin is on, " +
            "e.g. \"http://192.168.1.5:8080\". Exact match on scheme://host[:port].")]
        [SerializeField] private List<string> allowedOrigins = new List<string>();

        [Header("Tunnel")]
        [Tooltip("Let StartTunnel() download cloudflared if it isn't found. Default: on in the Editor, off in players — " +
            "a shipped game should opt in deliberately.")]
        [SerializeField] private bool tunnelAllowDownload = Application.isEditor;
        [Tooltip("Explicit cloudflared executable path (optional). Resolution order: this field → PMC_CLOUDFLARED " +
            "environment variable → PATH → the managed download cache.")]
        [SerializeField] private string cloudflaredPath = "";
        [Tooltip("\"quick\" (account-less, random trycloudflare URL) or \"named\" (your Cloudflare account, " +
            "stable hostname — set NamedTunnelToken and NamedTunnelHostname).")]
        [SerializeField] private string tunnelMode = "quick";
        [Tooltip("Named mode: token from the Cloudflare dashboard's \"run with token\" flow.")]
        [SerializeField] private string namedTunnelToken = "";
        [Tooltip("Named mode: the public hostname routed to the tunnel (e.g. party.example.com). The join URL is " +
            "built from it, since token mode prints no trycloudflare URL.")]
        [SerializeField] private string namedTunnelHostname = "";
        [Tooltip("Named mode: tunnel name for the credentials-file flow (run <name> instead of --token).")]
        [SerializeField] private string namedTunnelName = "";
        [Tooltip("Named mode: path to the tunnel credentials JSON for the credentials-file flow.")]
        [SerializeField] private string namedTunnelCredentialsFile = "";
        [Tooltip("Wait for the public hostname to resolve (DNS-over-HTTPS) before the tunnel reports ready. A QR scanned " +
            "before the name resolves can stick a phone on a cached NXDOMAIN for ~90 s.")]
        [SerializeField] private bool tunnelVerifyDns = true;
        [Tooltip("Seconds to wait for the tunnel to become ready before failing.")]
        [SerializeField] private float tunnelReadyTimeoutSec = 60f;
        [Tooltip("Extra cloudflared arguments (e.g. \"--protocol\", \"http2\" to force TCP on networks that block QUIC/UDP 7844).")]
        [SerializeField] private string[] tunnelExtraArgs = new string[0];
        [Tooltip("Join code used while a tunnel is up (StartTunnel's code argument wins). " +
            "Non-empty is used as-is and is never auto-cleared on StopTunnel.")]
        [SerializeField] private string tunnelJoinCode = "";
        [Tooltip("Automatically restart a tunnel that dropped after being ready (a new random URL is issued; " +
            "connected phones can't be told and must rescan). Bounded.")]
        [SerializeField] private bool tunnelAutoRestart = true;
        [Tooltip("Delay in seconds before an automatic tunnel restart.")]
        [SerializeField] private float tunnelRestartDelaySec = 2f;

        // ------------------------------------------------------------------ events
        // Re-raised from PmcHostCore with the same names, so `host.PlayerJoined += …` works without
        // touching .Host. All fire on the main thread inside Poll().

        /// <summary>A new player completed the hello handshake (or a tombstoned token came back).</summary>
        public event Action<PmcPlayer> PlayerJoined;
        /// <summary>A known player reconnected within the grace period (or replaced its own socket).</summary>
        public event Action<PmcPlayer> PlayerRejoined;
        /// <summary>A player's socket closed. They stay in Players() until GraceSeconds runs out.</summary>
        public event Action<PmcPlayer> PlayerDisconnected;
        /// <summary>A player's name or profile changed.</summary>
        public event Action<PmcPlayer> PlayerUpdated;
        /// <summary>A player was removed. Reason is "timeout", "kicked" or "leave".</summary>
        public event Action<PmcPlayer, string> PlayerLeft;
        /// <summary>A player sent the correct AdminPin.</summary>
        public event Action<PmcPlayer> AdminAuthenticated;
        /// <summary>A game message: the JSON d value of a msg frame, or a JToken of type Bytes for binary frames.</summary>
        public event Action<PmcPlayer, JToken> MessageReceived;
        /// <summary>The join URL changed (start, port, AdvertiseUrl, JoinCode, tunnel).</summary>
        public event Action<string> JoinUrlChanged;
        /// <summary>Fired once when no phone has joined within NoJoinsHintSeconds of the join URL being shown.</summary>
        public event Action NoJoinsHint;
        /// <summary>Tunnel progress: "downloading" | "starting" | "ready" | "lost" | "failed" | "stopped"; second arg is detail/URL.</summary>
        public event Action<string, string> TunnelStateChanged;

        // ------------------------------------------------------------------ state

        private PmcHostCore _host;

        /// <summary>
        /// The engine-free host. Created lazily on first access (or in Awake) with the serialized
        /// settings already applied — so code can wire events or tweak settings before Start.
        /// </summary>
        public PmcHostCore Host
        {
            get
            {
                EnsureCreated();
                return _host;
            }
        }

        /// <summary>Whether the server is listening.</summary>
        public bool Running { get { return _host != null && _host.Running; } }

        /// <summary>The port actually bound (0 when never started).</summary>
        public int BoundPort { get { return _host != null ? _host.BoundPort : 0; } }

        /// <summary>ControllerDir after resolution — the absolute path actually served.</summary>
        public string ResolvedControllerDir { get; private set; } = "";

        /// <summary>Call Start() in Unity's Start().</summary>
        public bool Autostart
        {
            get { return autostart; }
            set { autostart = value; }
        }

        /// <summary>Poll automatically from Update() / the edit-mode pump. Off = call Poll() yourself.</summary>
        public bool AutoPoll
        {
            get { return autoPoll; }
            set { autoPoll = value; }
        }

        /// <summary>Preferred TCP port (0 = ephemeral). Applied at Start.</summary>
        public int Port
        {
            get { return port; }
            set { port = value; if (_host != null) _host.Port = value; }
        }

        /// <summary>Join code; "" = no code. Updates the live host and the join URL.</summary>
        public string JoinCode
        {
            get { return joinCode; }
            set { joinCode = value; if (_host != null) _host.JoinCode = value; }
        }

        /// <summary>Admin PIN; "" = admin disabled.</summary>
        public string AdminPin
        {
            get { return adminPin; }
            set { adminPin = value; if (_host != null) _host.AdminPin = value; }
        }

        /// <summary>Join-URL base override; "" = auto LAN URL (or tunnel URL once ready).</summary>
        public string AdvertiseUrl
        {
            get { return advertiseUrl; }
            set { advertiseUrl = value; if (_host != null) _host.AdvertiseUrl = value; }
        }

        /// <summary>
        /// Directory served at "/", project-relative. See the Inspector tooltip / README for the
        /// resolution rules. Updating it re-resolves and updates the live host.
        /// </summary>
        public string ControllerDir
        {
            get { return controllerDir; }
            set
            {
                controllerDir = value;
                if (_host != null)
                {
                    ResolvedControllerDir = ResolveControllerDir(value);
                    _host.ControllerDir = ResolvedControllerDir;
                }
            }
        }

        // ------------------------------------------------------------------ lifecycle

        private void Awake()
        {
            EnsureCreated();
        }

        private void OnEnable()
        {
            EnsureCreated();
            PmcLiveHosts.Register(this);
#if UNITY_EDITOR
            EditorApplication.update -= EditorPump; // defensive: no double-subscribe
            EditorApplication.update += EditorPump;
#endif
        }

        private void Start()
        {
            if (autostart)
                StartHost();
        }

        private void Update()
        {
            // Edit mode is pumped by EditorApplication.update (see EditorPump); don't double-pump.
            if (Application.isPlaying && autoPoll)
                Poll();
        }

        private void OnDisable()
        {
#if UNITY_EDITOR
            EditorApplication.update -= EditorPump;
#endif
            PmcLiveHosts.Unregister(this);
            if (_host != null)
                _host.Stop();
        }

        private void OnDestroy()
        {
            PmcLiveHosts.Unregister(this);
            DisposeHost();
        }

        private void OnApplicationQuit()
        {
            DisposeHost();
        }

#if UNITY_EDITOR
        private void EditorPump()
        {
            if (Application.isPlaying || !autoPoll)
                return;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                return;
            Poll();
        }
#endif

        private void EnsureCreated()
        {
            if (_host != null)
                return;
            _host = new PmcHostCore();
            _host.PlayerJoined += p => { PlayerJoined?.Invoke(p); };
            _host.PlayerRejoined += p => { PlayerRejoined?.Invoke(p); };
            _host.PlayerDisconnected += p => { PlayerDisconnected?.Invoke(p); };
            _host.PlayerUpdated += p => { PlayerUpdated?.Invoke(p); };
            _host.PlayerLeft += (p, reason) => { PlayerLeft?.Invoke(p, reason); };
            _host.AdminAuthenticated += p => { AdminAuthenticated?.Invoke(p); };
            _host.MessageReceived += (p, data) => { MessageReceived?.Invoke(p, data); };
            _host.JoinUrlChanged += url => { JoinUrlChanged?.Invoke(url); };
            _host.NoJoinsHint += () => { NoJoinsHint?.Invoke(); };
            _host.TunnelStateChanged += (state, detail) => { TunnelStateChanged?.Invoke(state, detail); };
            PushSettings();
        }

        private void DisposeHost()
        {
            if (_host == null)
                return;
            try
            {
                _host.Stop();
            }
            catch (Exception)
            {
                // shutting down: never throw out of Unity messages
            }
            _host.Dispose();
            _host = null;
        }

        // ------------------------------------------------------------------ public API

        /// <summary>
        /// Pushes every serialized field into <see cref="Host"/>. Called once when the host is
        /// created; call it again yourself after changing the serialized fields from code.
        /// Preferably done while the host is stopped.
        /// </summary>
        public void ApplySettings()
        {
            EnsureCreated();
            PushSettings();
        }

        private void PushSettings()
        {
            PmcHostCore h = _host;
            h.Port = port;
            h.PortSearch = portSearch;
            h.BindAddress = bindAddress;
            ResolvedControllerDir = ResolveControllerDir(controllerDir);
            h.ControllerDir = ResolvedControllerDir;
            h.JoinCode = joinCode;
            h.MaxPlayers = maxPlayers;
            h.GraceSeconds = graceSeconds;
            h.RememberSeconds = rememberSeconds;
            h.HeartbeatSeconds = heartbeatSeconds;
            h.MaxMessageBytes = maxMessageBytes;
            h.AdminPin = adminPin;
            h.AdvertiseUrl = advertiseUrl;
            h.NoJoinsHintSeconds = noJoinsHintSeconds;
            h.IoBudgetMsec = ioBudgetMsec;
            h.MaxConnections = maxConnections;
            h.MaxConnectionsPerAddress = maxConnectionsPerAddress;
            h.JoinCodeMaxFailures = joinCodeMaxFailures;
            h.AdminPinMaxFailures = adminPinMaxFailures;
            h.CheckOrigin = checkOrigin;
            h.AllowedOrigins.Clear();
            h.AllowedOrigins.AddRange(allowedOrigins);
            h.TunnelAllowDownload = tunnelAllowDownload;
            h.CloudflaredPath = cloudflaredPath;
            h.TunnelMode = tunnelMode;
            h.NamedTunnelToken = namedTunnelToken;
            h.NamedTunnelHostname = namedTunnelHostname;
            h.NamedTunnelName = namedTunnelName;
            h.NamedTunnelCredentialsFile = namedTunnelCredentialsFile;
            h.TunnelVerifyDns = tunnelVerifyDns;
            h.TunnelReadyTimeoutSec = tunnelReadyTimeoutSec;
            h.TunnelExtraArgs = tunnelExtraArgs;
            h.TunnelJoinCode = tunnelJoinCode;
            h.TunnelAutoRestart = tunnelAutoRestart;
            h.TunnelRestartDelaySec = tunnelRestartDelaySec;
        }

        /// <summary>
        /// Applies the serialized settings and starts listening. Returns 0 on success, else an error
        /// code. To run with code-side tweaks instead, set them on <see cref="Host"/> and call
        /// <see cref="PmcHostCore.Start"/> yourself. Works in edit mode too (the Phone Controllers
        /// dock calls this).
        /// </summary>
        public int StartHost()
        {
            ApplySettings();
            return Host.Start();
        }

        /// <summary>Stops the server and closes every socket. A running tunnel is left up.</summary>
        public void StopHost()
        {
            if (_host != null)
                _host.Stop();
        }

        /// <summary>Drains queued I/O events now. Called per frame unless <see cref="AutoPoll"/> is off.</summary>
        public void Poll()
        {
            if (_host != null)
                _host.Poll();
        }

        /// <summary>The URL players open (advertise/tunnel URL, else http://&lt;best LAN IPv4&gt;:&lt;port&gt;/, plus ?code=).</summary>
        public string JoinUrl()
        {
            return Host.JoinUrl();
        }

        /// <summary>
        /// A QR code texture of <see cref="JoinUrl"/> — dark modules on white with a 4-module quiet
        /// zone, <paramref name="modulePx"/> px per module, point-filtered. Assign it to a RawImage.
        /// Null when the host isn't running.
        /// </summary>
        public Texture2D QrTexture(int modulePx = 8)
        {
            if (_host == null || !_host.Running)
                return null;
            return PmcQrTexture.Encode(_host.JoinUrl(), modulePx);
        }

        // ------------------------------------------------------------------ ControllerDir resolution

        /// <summary>
        /// Resolves <paramref name="dir"/> against the current environment:
        /// absolute paths pass through; "StreamingAssets[/…]" maps under
        /// <see cref="Application.streamingAssetsPath"/>; project-relative paths resolve against the
        /// project in the Editor and against StreamingAssets/pmc/&lt;folder name&gt; in a player build
        /// (where the build preprocessor copies them).
        /// </summary>
        public static string ResolveControllerDir(string dir)
        {
            return ResolveControllerDir(dir, Application.streamingAssetsPath, Application.isEditor);
        }

        /// <summary>Environment-independent overload, for tests and the editor preprocessor.</summary>
        public static string ResolveControllerDir(string dir, string streamingAssetsPath, bool runningInEditor)
        {
            if (string.IsNullOrEmpty(dir))
                return "";
            dir = dir.Trim().Replace('\\', '/');
            if (Path.IsPathRooted(dir) || dir.Contains("://"))
                return dir;
            if (dir == "StreamingAssets")
                return streamingAssetsPath;
            if (dir.StartsWith("StreamingAssets/", StringComparison.Ordinal))
                return JoinPath(streamingAssetsPath, dir.Substring("StreamingAssets/".Length));
            if (runningInEditor)
                return Path.GetFullPath(dir);
            // Player build: the build preprocessor copies every served project directory to
            // StreamingAssets/pmc/<folder name>, so only the basename survives.
            string trimmed = dir.TrimEnd('/');
            string name = trimmed.Substring(trimmed.LastIndexOf('/') + 1);
            return JoinPath(JoinPath(streamingAssetsPath, "pmc"), name);
        }

        private static string JoinPath(string a, string b)
        {
            return a.TrimEnd('/', '\\') + "/" + b.TrimStart('/', '\\');
        }
    }
}
