# unity-phone-mass-controllers — spec (v1)

A pure-C# Unity package that turns a Unity game into the host for a room full of
phone controllers. The idea comes from HappyFunTimes. Players scan a QR code, their phone's
browser loads a controller page served **by the game itself**, and they're connected over
WebSocket. No app install, no Node, no external server.

Game authority lives in Unity. The package handles transport, identity/rejoin, the QR code,
the join URL (LAN or one-click Cloudflare tunnel), and a small JS SDK for controller pages.
**The wire protocol is identical to the Godot port (`godot-phone-mass-controllers`)** — the
same `pmc.js`, the same `pmc.*` messages; a phone cannot tell which engine is hosting.

- Engine: Unity **2021.3+**, developed/tested on **Unity 6 (6000.3.24f1)**.
- Pure C#. No native binaries in the repo, no third-party asset dependencies.
  JSON via `com.unity.nuget.newtonsoft-json`.
- License: MIT. Repo: `splatterfacegames/unity-phone-mass-controllers`.
- Package: `Packages/com.splatterfacegames.phone-mass-controllers/` — namespace `Splatter.Pmc`.
- C# surface is pinned in [docs/API_CONTRACT.md](docs/API_CONTRACT.md).

## 1. Transport

One TCP port serves both HTTP/1.1 and WebSocket (RFC 6455), so a single tunnel hostname works.

- The addon has its own HTTP/1.1 request parser and WebSocket server implementation on `TCPServer` +
  `StreamPeerTCP`. Godot's `WebSocketPeer.accept_stream` can't share a port with HTTP once the
  headers have been read.
- WS upgrade path: `GET /pmc/ws`. Everything else is HTTP.
- WS must support:
  - masking (client→server required)
  - text + binary frames
  - fragmentation / continuation reassembly
  - ping/pong control frames
  - close handshake with codes
  - payloads up to `max_message_bytes` (default 1 MiB)
  - rejecting unmasked client frames and oversize messages
- No extensions (permessage-deflate is not negotiated).
- HTTP must support:
  - GET/HEAD
  - keep-alive
  - `Content-Length` responses, and chunked or streamed large file writes without blocking the frame
  - 400/404/405/413/500
  - path-traversal protection
  - MIME by extension
  - `Cache-Control: no-cache` for html/js
- Non-blocking. Poll from `_process` (or an optional internal thread; default is main thread).
  Per-frame I/O budget, so a slow phone can't stall the game.
- `bind_address` default `"*"`. `port` default 8080. If busy, try the next port up to `port + port_search`
  (default 20), and emit/return the actual port.
- Delivery order is guaranteed **per player (per socket), not across players**: `send`/`broadcast` queue
  frames that are flushed in the host's round-robin service order, so a frame queued for socket A and then
  one for socket B can reach B first. Tests and game logic must not rely on cross-player arrival order.

Known limits of the built-in server (deliberate scope cuts — use the Cloudflare tunnel for `https`/`wss`):

- **No TLS.** Plain `http`/`ws` on the LAN. `start_tunnel()` is the supported way to get TLS.
- **No permessage-deflate** — extensions are never negotiated.
- **Request bodies:** `Content-Length` only; `Transfer-Encoding: chunked` gets 501.
- **Caching:** no `ETag`/`If-None-Match`. `Range` supports a single range; multi-ranges are ignored (200).
- **Symlinks** in served trees are resolved and confined to the served root (403 on escape).
- **Reverse proxies:** only `CF-Connecting-IP` is trusted, and only while the tunnel is up. `Forwarded`/
  `X-Forwarded-For` are ignored, so per-address limits behind another proxy see the proxy's address.
- **Origin:** not checked on the WS upgrade by default; `check_origin` + `allowed_origins` opt in.
- The join URL is IPv4-only (see §3 `lan_addresses`).

## 2. Wire protocol

Text frames are JSON objects with a string field `t`. Types `pmc.*` are reserved for the addon.
Game messages use `t: "msg"` with the payload in `d`. **Binary frames are raw game payloads** (e.g.
FlatBuffers), passed through untouched in both directions.

Client → host:

| t | fields | notes |
|---|--------|-------|
| `pmc.hello` | `sdk:1`, `token?:string`, `name?:string`, `profile?:object`, `code?:string` | must be the first frame. `token` = rejoin; `code` defaults from `?code=` in the page URL |
| `pmc.ping` | `c:number` (client epoch ms) | |
| `pmc.profile` | `name?`, `profile?` | update identity |
| `pmc.auth` | `pin:string` | admin elevation |
| `pmc.leave` | | explicit leave (no grace) |
| `msg` | `d:any` | game message |

Host → client:

| t | fields | notes |
|---|--------|-------|
| `pmc.welcome` | `id:int`, `token:string`, `name`, `profile`, `rejoined:bool`, `admin:bool`, `server_ms:int` (epoch ms UTC), `join_url:string` | |
| `pmc.reject` | `code:string` (`bad_code`, `full`, `version`, `banned`, `bad_hello`), `reason:string` | then close 4000 |
| `pmc.pong` | `c`, `s:int` (epoch ms UTC) | clock-offset estimate; `s` is wall-clock epoch ms, so `serverNow()` is comparable to `turn_ends_at_ms`-style deadlines stamped from `Time.get_unix_time_from_system() * 1000` |
| `pmc.auth` | `ok:bool`, `locked_ms?:int`, `disabled?:bool` | 5 failures → 30 s per connection; 20 per address → 60 s; `admin_pin_max_failures` (default 20) across all addresses disables the PIN until restart (`disabled:true`) |
| `pmc.kicked` | `reason:string` | then close 4001 |
| `pmc.replaced` | | same token connected elsewhere, then close 4002 |
| `pmc.moved` | `d.url:string` | join URL changed mid-session (tunnel replaced); sent before the old tunnel goes down. https→https pages may auto-follow; others should show "rescan/rejoin at the new URL" |
| `msg` | `d:any` | game message |

HTTP auth for custom routes: after join, pmc.js sets a `pmc_token` cookie (value = the rejoin token).
`require_player(req)` maps `?t=<token>` or that cookie to the joined player; serve nothing private without it.

While a tunnel is up the host is public: auto-generated join codes are 6 chars (24^6), and
`/pmc/info.json` + `/pmc/qr.png` (which reveal the join URL) answer only to loopback or a request
carrying a valid `?code=`. The controller page and `/pmc/pmc.js` stay public — phones need them to join.

Identity: the token is a random 128-bit hex string issued by the host. The same token reconnecting within
`grace_seconds` resumes the same `PMCPlayer` (same id, meta preserved) and emits `player_rejoined`. After grace
expires the player is removed with `player_left(player, "timeout")`. A token seen after removal starts
a new player, unless `remember_seconds` (default 3600) keeps a tombstone so the id and meta come back.
Tombstones are written only on `"timeout"` removal (and on `kick(..., remember := true)`) — a plain
`kick()` or `pmc.leave` drops the token, so the player comes back as someone new (new id, empty meta).

## 3. C# API

Authoritative signatures: [docs/API_CONTRACT.md](docs/API_CONTRACT.md). Shape for orientation:

```csharp
// glue — what a Unity dev puts on a GameObject
[ExecuteAlways]
[AddComponentMenu("Splatter/Phone Mass Controllers")]
public sealed class PmcHost : MonoBehaviour {
    // [SerializeField] mirror of every PmcHostCore setting; pushed into Host when it's
    // created (Awake/first access). ApplySettings() re-pushes; Host exists before Start()
    // so code can wire events early. Every core event is re-raised on PmcHost under the
    // same name, so `host.PlayerJoined += …` works without touching .Host.
    public PmcHostCore Host { get; }
    public bool Running { get; }  public int BoundPort { get; }
    public string ResolvedControllerDir { get; }
    public int StartHost();       // ApplySettings + Host.Start() — works in edit mode too
    public void StopHost();       public void Poll();
    public string JoinUrl();
    public Texture2D QrTexture(int modulePx = 8);   // PmcQrMatrix → Texture2D; null until Running
    // Start() → StartHost() when Autostart; Update() → Host.Poll() — all host events fire
    // on the main thread there. Edit mode: EditorApplication.update pumps it instead.
    // OnEnable/OnDisable register it in PmcLiveHosts.All (the dock + tests read it);
    // OnDestroy/OnDisable stop the host; OnApplicationQuit disposes it.
}
public static class PmcLiveHosts { public static IReadOnlyList<PmcHost> All; }
```

```csharp
// core — engine-free; usable in tests, headless servers, editor pumps
public sealed class PmcHostCore : IDisposable {
    // settings (mirror the Godot exports): Port, PortSearch, BindAddress, ControllerDir,
    // JoinCode, MaxPlayers, GraceSeconds, RememberSeconds, HeartbeatSeconds,
    // MaxMessageBytes, AdminPin, AdvertiseUrl, NoJoinsHintSeconds, IoBudgetMsec,
    // MaxConnections, MaxConnectionsPerAddress, JoinCodeMaxFailures, AdminPinMaxFailures,
    // CheckOrigin + AllowedOrigins
    int Start(); void Stop(); void Poll(float budgetMs = -1); bool Running;
    int BoundPort { get; } string JoinUrl(); List<string> LanAddresses();
    // events: PlayerJoined/Rejoined/Disconnected/Updated, PlayerLeft, AdminAuthenticated,
    //         MessageReceived(player, JToken|byte[]), JoinUrlChanged, NoJoinsHint,
    //         TunnelStateChanged(state, url) — all raised inside Poll(), main-thread safe
    List<PmcPlayer> Players(bool includeDisconnected = true); PmcPlayer GetPlayer(int id);
    void Send(PmcPlayer to, object data);              // JToken/dict/list/scalar → JSON msg; byte[] → binary
    void Broadcast(object data, Func<PmcPlayer,bool> filter = null);
    void Kick(PmcPlayer to, string reason = "", bool ban = false, bool remember = false);
    void AddRoute(string prefix, Func<PmcHttpRequest,PmcHttpResponse> handler);
    void ServeDirectory(string prefix, string dir, bool playersOnly = false);
    PmcPlayer RequirePlayer(PmcHttpRequest req); JObject GetStats(); string InfoJson();
    void StartTunnel(string code = ""); void RestartTunnel(string code = "");
    void StopTunnel(); PmcTunnel GetTunnel();
}
```

Optional lobby helpers (pure logic, no networking): `PmcQueue`, `PmcVote`, `PmcRotation`
— same semantics as the Godot versions (`pop_next` skips ineligible, majority-at-timeout
vote expiry, winner/loser/strict rotation with max-streak).

## 4. JS SDK — served at `/pmc/pmc.js` (ES module) + `pmc.d.ts`

```js
import { connect, feedback, keepScreenOn, vibrate, wakeLock } from '/pmc/pmc.js';
const pmc = connect({ name, profile, code /* default: ?code= from location */, tokenKey });
pmc.on('welcome', ({id, rejoined}) => {}); pmc.on('message', d => {}); pmc.on('binary', ab => {});
pmc.on('status', s => {});            // 'connecting' | 'open' | 'reconnecting' | 'closed'
pmc.on('reject', ({code, reason}) => {}); pmc.on('kicked', r => {}); pmc.on('replaced', () => {});
pmc.on('moved', ({url}) => {});       // join URL changed (ephemeral tunnel)
pmc.send(obj); pmc.sendBinary(arrayBufferOrView); pmc.auth(pin) /* Promise<boolean> */;
pmc.setProfile({name, profile}); pmc.leave();
pmc.id; pmc.serverNow(); pmc.timestamp();  // host-clock ms, offset-corrected
pmc.rttMs;                            // rolling avg round-trip ms
```
- Token in localStorage, keyed by origin (two tabs in one browser = same player; `tokenKey` overrides for
  per-tab identities). Also mirrored to a `pmc_token` cookie (`path=/`, `SameSite=Strict`) that gated custom
  routes can check. Reconnect with jittered exponential backoff, capped at 5 s.
- `wss:` when the page is https (tunnel).
- Never retry after `reject`/`kicked`/`replaced`.
- `pmc.moved`: auto-follow only https→https after a reachability check; a LAN (`http://`) page never navigates
  itself — show "re-scan the QR" on the `moved` event.
- `vibrate(pattern)` feature-detects (absent on iOS). `feedback('buzz'|'success'|'error')` vibrates where
  possible, else a 60 ms screen flash + WebAudio click (audio only after a user gesture).
- `wakeLock()` requests a screen wake lock (secure contexts only). `keepScreenOn()` uses it where allowed and
  otherwise plays a muted looping clip on the next user gesture (NoSleep-style).
- `timestamp()` stamps inputs on the estimated host clock; hosts should credit them bounded by the player's
  RTT (also exposed as `player.rtt_ms`) so remote players stay competitive.
- Zero dependencies, no build step for consumers.

## 5. Outside-LAN join: Cloudflare tunnels (quick + named)

`PMCTunnel` (used by `PMCHost.start_tunnel()` / `restart_tunnel()` and the editor dock):

1. Resolve `cloudflared`: export var path → env `PMC_CLOUDFLARED` → `PATH` → `user://pmc/bin/cloudflared[.exe]`.
2. If missing, download the official release asset for the OS/arch from
   `https://github.com/cloudflare/cloudflared/releases/latest/download/…` (windows-amd64.exe, linux-amd64,
   linux-arm64, darwin `.tgz`, extracted via `tar`). The binary must then pass: SHA-256 vs the release's GitHub
   API digest, `cloudflared --version` ≥ `minimum_version` (default 2022.6.2), and an OS code-signature check
   where supported (Windows Authenticode — must be Valid and signed by Cloudflare, Inc. when a signature is
   present; macOS `codesign --verify`, with `spctl` for notarization; unsigned binaries fall back to the digest
   check — Linux users should prefer their distro's signed Cloudflare package). A managed binary older than
   `binary_max_age_days` (default 30) is re-verified against the latest release and refreshed when it drifted.
   Download requires `allow_download=true` (default: editor on, runtime off unless the game opts in).
3. **Quick mode** runs `cloudflared tunnel --no-autoupdate --config <isolated> --url http://127.0.0.1:<port>`
   via `OS.execute_with_pipe`. The always-isolated `--config` keeps a default `~/.cloudflared/config.yml`
   (left over from named-tunnel setups) from breaking the quick tunnel. stderr is read for
   `https://<random>.trycloudflare.com` and "Registered tunnel connection"; with `verify_dns` the hostname must
   also resolve over DNS-over-HTTPS before `ready`.
   **Named mode** runs `cloudflared tunnel --no-autoupdate run --token <named_token>`, or — with
   `named_tunnel` + `named_credentials_file` — a generated `named-tunnel.yml` (ingress hostname →
   `http://127.0.0.1:<port>`) and `run <named_tunnel>`. No trycloudflare URL is printed; the URL is
   `https://<named_hostname>`.
4. States: `downloading` → `starting` → `ready` → `lost` (every edge connection unregistered for
   `lost_grace_sec`, or the process exits post-ready; an alive process can return to `ready` on
   re-registration) → `stopped` / `failed`. Creation failures (HTTP 429 / error 1015) retry with exponential
   backoff (`max_retries` default 2, `retry_backoff_sec` default 4 s). When QUIC (UDP 7844) looks blocked —
   its signature errors in the log, or registration stalling past `protocol_fallback_sec` after the URL was
   issued — cloudflared relaunches once with `--protocol http2` (skipped when `extra_args` already sets a
   protocol). Common error lines map to actionable hints ("rate-limited", "UDP blocked", "DNS filter").
5. On `ready`: `advertise_url` = tunnel URL, `join_url_changed`, QR regenerates. `join_code` empty →
   **auto-generate a 6-letter code** (24^6 ≈ 191M — the host is now on the public internet); the QR and
   `join_url()` carry it as `?code=`, and `/pmc/info.json` + `/pmc/qr.png` (which reveal the URL) answer only to
   loopback or a valid `?code=`. `start_tunnel(code)` or `tunnel_join_code` supplies a code that is used as-is
   and never auto-cleared. The QR/join URL is only ever shown post-`ready` — a phone that resolves a
   brand-new hostname too early can sit on a cached NXDOMAIN for ~90 s (fix: airplane-mode toggle or wait).
6. Lifecycle: `stop_tunnel()` and freeing the host kill the child process; the pid is recorded in
   `user://pmc/cloudflared.pid` and a leftover from a crashed engine is reaped on the next `start()` — only
   when the pid's command line still looks like cloudflared, so a recycled pid is never killed. A `ready`
   tunnel survives `stop()`→`start()` on the same port (retargeted when the port changed) and a host
   teardown detaches it to the scene root, where the next `start_tunnel()` re-adopts it within ~2 minutes.
7. `restart_tunnel()` performs a rolling restart: the replacement tunnel reaches `ready` first, all joined
   players get `{"t":"pmc.moved","d":{"url":<new join url>}}`, then the old tunnel stops. A `lost` tunnel is
   auto-restarted by the host (`tunnel_auto_restart`, `tunnel_restart_delay_sec`, bounded per `start_tunnel`).

Quick Tunnels need no Cloudflare account, but URLs are ephemeral, best-effort, rate-limited (HTTP 429,
~200 concurrent in-flight requests, no SSE) and come with no uptime guarantee — use a named tunnel (or
another provider, see docs/tunnels.md) for anything you want to print or keep.

## 6. Unity editor integration

`PmcHost` is a `MonoBehaviour` in *Add Component → Splatter → Phone Mass Controllers*. The package adds a
**Window → Phone Controllers** dockable window that shows live host status and offers "Download cloudflared",
a throwaway test tunnel, and docs links.

- **Live status.** Play mode runs in-process, so the dock reads `PmcLiveHosts.All` — a static registry each
  enabled `PmcHost` joins (OnEnable) and leaves (OnDisable/OnDestroy) — directly: port, join URL + QR
  preview, connected players, tunnel state. No debugger channel needed (simpler than the Godot version).
- **Edit-mode hosting.** `PmcHost` is `[ExecuteAlways]` and can run in edit mode (useful for testing
  controllers without Play, and it's how the dock drives its test tunnel): the same pump runs off
  `EditorApplication.update` inside `#if UNITY_EDITOR`, gated by `Application.isPlaying` so Update()
  never double-pumps in Play.
- **ControllerDir.** Serialized as a project-relative string (`Assets/…`/`Packages/…` —
  Inspector-friendly, portable). `PmcHost.ResolveControllerDir` maps it: `StreamingAssets[/…]` →
  under `Application.streamingAssetsPath`; rooted/`scheme://` paths pass through; anything else is a
  project path — resolved against the project in the Editor, and in a player build to
  `StreamingAssets/pmc/<folder name>` where the build preprocessor copied it. The serialized value
  never changes; `PmcHost.ResolvedControllerDir` reports the resolved one.
- **Builds.** A build preprocessor copies each `PmcHost`'s `ControllerDir` (project-relative) plus the
  package `Web/` SDK into `StreamingAssets/pmc/` before a player build, so served files survive import
  settings. ServeDirectory paths inside `Assets/` resolve the same way at runtime.

## 7. Repo layout

```
Packages/com.splatterfacegames.phone-mass-controllers/
  package.json                 UPM manifest (install via git URL ?path=)
  Runtime/Splatter.Pmc.Core.asmdef   engine-free core (noEngineReferences) — tests run in plain dotnet
  Runtime/Core/{Http,Ws,Host,Tunnel,Qr,Lobby}/   the port
  Runtime/{PmcHost.cs,PmcLiveHosts.cs,…}         Unity glue (Splatter.Pmc.asmdef)
  Web/pmc.js + pmc.d.ts        the shared browser SDK (byte-identical to Godot's where possible)
  Editor/                      dock window + build preprocessor (Splatter.Pmc.Editor.asmdef)
  Samples~/BuzzerParty/        UPM sample: scene + scripts + controller page
Assets/Demo/                   in-repo dev demo (same content as the sample)
tests/dotnet/                  NUnit suite compiling Runtime/Core/** directly (CI runs it w/o Unity)
tests/node/                    ws-interop + QR-decode corpus (reused from the Godot repo)
Tests~ or Tests/               Unity Test Framework edit/play-mode suites for glue code
tools/                         make_unitypackage.py + helpers
site/                          GitHub Pages site (pmc-unity.jethachan.net)
.github/workflows/             ci.yml, pages.yml, release.yml
README.md  LICENSE  CHANGELOG.md  SPEC.md  docs/
```

## 8. Quality bar

- **Interop:** Node `ws` client passes (text, binary, fragmented, 1 MiB, ping, close) against the C# host;
  `pmc.js` + demo controller run in a real browser (Playwright optional in CI).
- **QR:** encoder matrices for a corpus of URLs (versions 1–10, EC M) decoded by `jsqr` in tests/node.
- **Robustness:** malformed HTTP, garbage WS frames, partial headers (timeout), 200 concurrent sockets —
  no crash, no stall; Poll() drain budget bounded; `LogAssert` used so expected socket errors don't fail runs.
- CI: `dotnet test` for the core (no Unity license needed), Node interop + QR on ubuntu, `.unitypackage`
  built by `tools/make_unitypackage.py` and attached to releases; a Unity batchmode compile check runs
  when `UNITY_LICENSE` is configured.
- The public repo contains no third-party game assets and no references to any private project.
