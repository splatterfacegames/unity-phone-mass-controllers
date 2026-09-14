# API contract — unity-phone-mass-controllers v0.1.0

Port of `godot-phone-mass-controllers` to Unity. **Same wire protocol** — pmc.js, the `pmc.*`
messages, and the HTTP routes are identical; a Godot host and a Unity host are interchangeable to
a phone. Reference sources (read them): `G:/_projects/xiv/godot-phone-mass-controllers/` —
`addons/phone_mass_controllers/*.gd`, `SPEC.md`, `tests/`.

## Assemblies

- `Splatter.Pmc.Core` (`Runtime/Core/…`): `noEngineReferences: true` — **zero UnityEngine types**.
  Pure C#, .NET Standard 2.1 API surface (also compiled+tested by `tests/dotnet` in CI — keep it
  engine-free and C# 9 compatible: no records, no `required`, no collection expressions, no
  `DateOnly`). JSON = Newtonsoft (`JObject`/`JArray`/`JToken`).
- `Splatter.Pmc` (`Runtime/…` outside `Core/`): Unity glue — `PmcHost : MonoBehaviour`,
  `PmcQrTexture`, static host registry. Depends on Core.
- `Splatter.Pmc.Editor` (`Editor/`): dock window + build preprocessor. Depends on both.

## Namespaces

`Splatter.Pmc` for everything (Core + glue). `Splatter.Pmc.Editor` for editor code.

## Core public surface (exact signatures — implement/consume verbatim)

```csharp
public sealed class PmcPlayer {
    public int Id; public string Token; public string Name;
    public JObject Profile = new JObject();
    public bool Connected; public bool IsAdmin;
    public JObject Meta = new JObject();                 // game data, kept across rejoin
    public long JoinedMs; public long LastSeenMs; public long GraceDeadlineMs;
    public string RemoteAddress = ""; public float RttMs; // EMA of WS ping→pong
}

public sealed class PmcHostCore : IDisposable {
    // --- settings (set before Start) ---
    int Port { get; set; }                              // default 8080
    int PortSearch { get; set; }                        // ports to try from Port, default 20
    string BindAddress { get; set; }                    // default "*"
    string ControllerDir { get; set; }                  // served at "/" (abs path)
    string JoinCode { get; set; }                       // "" = no code
    int MaxPlayers { get; set; }                        // default 0 = unlimited
    float GraceSeconds { get; set; }                    // rejoin grace, default 30
    float RememberSeconds { get; set; }                 // tombstone TTL, default 3600
    float HeartbeatSeconds { get; set; }                // WS ping, default 15; dead after 2 misses
    int MaxMessageBytes { get; set; }                   // default 1<<20
    string AdminPin { get; set; }                       // "" = admin disabled
    string AdvertiseUrl { get; set; }                   // "" = auto LAN URL
    float NoJoinsHintSeconds { get; set; }              // 0 = off
    float IoBudgetMsec { get; set; }                    // Poll() drain budget, default 8
    int MaxConnections { get; set; }                    // default 0 = unlimited
    int MaxConnectionsPerAddress { get; set; }          // default 8
    int JoinCodeMaxFailures { get; set; }               // per address → 60 s block, default 10
    int AdminPinMaxFailures { get; set; }               // global budget, default 20 → PIN dead
    bool CheckOrigin { get; set; }                      // default false
    List<string> AllowedOrigins { get; }                // exact-match list
    // --- tunnel settings (applied pre-launch; see PmcTunnel) ---
    bool TunnelAllowDownload { get; set; }              // editor-default true, runtime false
    string CloudflaredPath { get; set; }
    string TunnelMode { get; set; }                     // "quick" | "named"
    string NamedTunnelToken { get; set; }
    string NamedTunnelHostname { get; set; }
    string NamedTunnelName { get; set; }
    string NamedTunnelCredentialsFile { get; set; }
    bool TunnelVerifyDns { get; set; }                  // default true
    float TunnelReadyTimeoutSec { get; set; }           // default 60
    string[] TunnelExtraArgs { get; set; }
    string TunnelJoinCode { get; set; }                 // used as-is, never auto-cleared
    bool TunnelAutoRestart { get; set; }                // default true
    float TunnelRestartDelaySec { get; set; }           // default 2

    // --- lifecycle ---
    int Start();                                        // 0 = OK, else an error code
    void Stop();
    void Poll(float budgetMs = -1);                     // drain queued I/O events; call per frame
    bool Running { get; }
    int BoundPort { get; }                              // actual bound port
    string JoinUrl();                                   // advertise or LAN url + ?code=
    string LocalIp { get; }
    List<string> LanAddresses();

    // --- events (raised on the thread calling Poll) ---
    event Action<PmcPlayer> PlayerJoined, PlayerRejoined, PlayerDisconnected, PlayerUpdated;
    event Action<PmcPlayer, string> PlayerLeft;
    event Action<PmcPlayer> AdminAuthenticated;
    event Action<PmcPlayer, JToken> MessageReceived;    // JToken or byte[] for binary frames
    event Action<string> JoinUrlChanged;
    event Action NoJoinsHint;
    event Action<string, string> TunnelStateChanged;    // state, detail/url

    // --- players ---
    List<PmcPlayer> Players(bool includeDisconnected = true);
    PmcPlayer GetPlayer(int id);
    void Send(PmcPlayer to, object data);               // JToken/IDictionary/IList/string/number → {"t":"msg","d":...}; byte[] → binary frame
    void Broadcast(object data, Func<PmcPlayer, bool> filter = null);
    void Kick(PmcPlayer to, string reason = "", bool ban = false, bool remember = false);

    // --- HTTP ---
    void AddRoute(string prefix, Func<PmcHttpRequest, PmcHttpResponse> handler); // null → fall through
    void ServeDirectory(string prefix, string dir, bool playersOnly = false);    // symlink-confined
    PmcPlayer RequirePlayer(PmcHttpRequest req);        // ?t= or pmc_token cookie; null after writing 403
    JObject GetStats();
    string InfoJson();                                  // the /pmc/info.json payload (loopback/code-gated while tunneled)

    // --- tunnel ---
    void StartTunnel(string code = "");
    void RestartTunnel(string code = "");
    void StopTunnel();
    PmcTunnel GetTunnel();
}
```

```csharp
public sealed class PmcTunnel {                       // engine-free; pumped by host Poll
    string State { get; }                             // "idle"|"downloading"|"starting"|"ready"|"lost"|"failed"|"stopped"
    string Url { get; }                               // public join base once ready
    string LastError { get; }
    List<string> LogLines { get; }
    event Action<string, string> StateChanged;        // marshalled to host Poll thread
    int Start(int localPort);                         // resolve→(download)→launch; returns 0/err
    void Stop();
    void Pump();                                      // drain process-reader queues (host calls it)
    // options — set before Start; mirrored from host exports
    string Mode; string NamedToken; string NamedHostname; string NamedName; string NamedCredentialsFile;
    string BinaryPath; bool AllowDownload; bool VerifyDns; float ReadyTimeoutSec; string[] ExtraArgs;
    int MaxRetries; float RetryBackoffSec; float ProtocolFallbackSec; float LostGraceSec;
    int BinaryMaxAgeDays; string MinimumVersion;
}
```

```csharp
public sealed class PmcHttpRequest {                  // method/path/query/headers/body
    string Method, Path, RawTarget; NameValueCollection Query;   // or Dictionary<string,string>
    Dictionary<string,string> Headers; byte[] Body; string RemoteAddress;
}
public sealed class PmcHttpResponse {
    int Status; Dictionary<string,string> Headers; byte[] Body;
    static PmcHttpResponse Text(int status, string s);  // + Json(JToken), Bytes(...), NotFound()…
}
```

```csharp
public sealed class PmcQrMatrix {                     // a finished symbol
    int Size { get; } int Version { get; } int Ecc { get; } int Mask { get; } string Mode { get; }
    bool this[int x, int y] { get; }                  // true = dark module
    string[] ToRows();                                // "0101" rows for test corpora
}
public static class PmcQr {
    enum Ecc { L = 0, M = 1, Q = 2, H = 3 }
    static PmcQrMatrix Encode(string text, Ecc ecc = Ecc.M);
    static PmcQrMatrix EncodeAdvanced(string text, Ecc ecc = Ecc.M,
        int minVersion = 1, int maxVersion = 40, int mask = -1, string mode = "");
                                                // mode: ""|"byte"|"alphanumeric"|"numeric"; null on failure
    static byte[] EncodePng(PmcQrMatrix m, int modulePx = 4, int quiet = 4);   // minimal PNG, no engine deps
}
```

```csharp
public sealed class PmcQueue { Push/Remove/Position/Ids/PopNext(n, Func<int,bool> eligible) … }
public sealed class PmcVote  { Open(proposalId, eligibleIds, endsMs, vetoers)/Cast(id,approve)/State… }
public sealed class PmcRotation { Next(winner, loser) strategies: WinnerStays/LoserStays/Strict }
```
(Exact member names follow the Godot lobby sources — port them faithfully.)

## Wire protocol (identical to Godot — port `SPEC.md` semantics)

- Same port: HTTP/1.1 + WS upgrade; `/` = controller dir, `/pmc/pmc.js`, `/pmc/qr.png`,
  `/pmc/info.json`, `/ws` = socket.
- Client→host: `pmc.hello`(`sdk:1,token?,name?,profile?,code?`), `pmc.ping`(`c` epoch ms),
  `pmc.profile`, `pmc.auth`(`pin`), `pmc.leave`, `msg`(`d`).
- Host→client: `pmc.welcome`(`id,token,name,profile,rejoined,admin,server_ms`(epoch ms),`join_url`),
  `pmc.reject`(`code,reason`→close 4000), `pmc.pong`(`c,s` epoch ms), `pmc.auth`(`ok,locked_ms?,disabled?`),
  `pmc.kicked`(→4001), `pmc.replaced`(→4002), `pmc.moved`(`d.url`), `msg`.
- Limits: per-IP join-code failures → 60 s block (trust `CF-Connecting-IP` only from loopback while
  tunneled); admin PIN 5/conn → 30 s, 20/address → 60 s, global `AdminPinMaxFailures` disables;
  6-char codes auto-generated while tunneled (24^6); `info.json`/`qr.png` loopback-or-`?code=` while
  tunneled; `pmc_token` cookie set post-join; send order per-socket only.
- I/O model: dedicated socket thread does accept/read/write/frame-decode; `Poll()` drains complete
  events on the caller's thread within `IoBudgetMsec`. (One mode only — the Godot io-thread design,
  always on; document it.)

## Unity glue surface

```csharp
[AddComponentMenu("Splatter/Phone Mass Controllers")]
public sealed class PmcHost : MonoBehaviour {
    // [SerializeField] mirrors of every PmcHostCore setting, pushed into Host on Awake/Start
    public PmcHostCore Host { get; }                  // events re-raised as UnityEvent-agnostic Action<>
    public Texture2D QrTexture(int modulePx = 8);     // PmcQr matrix → Texture2D
    // Update() → Host.Poll();  edit-mode pump via EditorApplication.update under #if UNITY_EDITOR
    // registers itself in PmcLiveHosts.All (static registry the dock reads)
}
```

## Files each stream owns — see your prompt. Golden rules:

- Tabs → **4 spaces**, file-scoped namespaces allowed (C# 10+? NO — stay C# 9: block namespaces, no
  file-scoped), braces Allman or K&R per file consistency — match the file you're in; new files use
  `namespace Splatter.Pmc { ... }` block style.
- No `UnityEngine`/`UnityEditor` inside `Runtime/Core/**` or `tests/dotnet` code paths.
- Deterministic behavior under `Poll()` — never let worker threads touch events directly.
- Tests: NUnit under `tests/dotnet/` (pure core) AND/OR `Tests~/EditMode|PlayMode` (Unity Test
  Framework) — every feature needs coverage somewhere; prefer dotnet tests when pure.
