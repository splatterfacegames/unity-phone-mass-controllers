# Outside-LAN providers: tunnels, relays, and how to add one

`PmcHostCore.StartTunnel()` shares the host beyond the LAN. `PmcTunnel` is the built-in provider and
supports two Cloudflare modes; this page covers both, the alternatives, and the provider contract a new
backend must satisfy.

> **Desktop only.** `PmcTunnel` spawns `cloudflared` as a child process — that can't run on Android,
> iOS, WebGL, or console builds, so `StartTunnel` reports `failed` there (`PmcTunnel.UnsupportedReason()`
> explains why). For those platforms you need a relay instead — see [relay-scope.md](relay-scope.md).

## Quick tunnels (default, no account)

```csharp
host.TunnelAllowDownload = true;
host.StartTunnel();          // https://<random>.trycloudflare.com, join code auto-generated
```

`cloudflared tunnel --no-autoupdate --url http://127.0.0.1:<port>` runs as a child process (always with an
isolated `--config`, so a leftover `~/.cloudflared/config.yml` can't break it — a default config file
otherwise prevents quick tunnels from starting). Limits: new random URL every start, no SLA, possible
HTTP 429 / error 1015 rate limiting (retried with backoff — `MaxRetries`, `RetryBackoffSec`), ~200
concurrent in-flight requests per tunnel, no SSE. WebSockets work. Networks that block QUIC/UDP 7844 are
retried once over HTTP/2 (`--protocol http2`, TCP 443) — force it via `TunnelExtraArgs`.

## Named tunnels (stable URL, Cloudflare account)

A named tunnel gives `https://party.example.com` — the same join URL every session, printable on cards.

One-time setup, easiest path (token):

1. Cloudflare account + a zone (your domain) on Cloudflare DNS.
2. Zero Trust dashboard → Networks → Tunnels → *Add a tunnel* → cloudflared.
3. Under "Install and run", copy the **token** from `cloudflared tunnel run --token <T>`.
4. In the tunnel's *Public Hostnames*, route `party.example.com` → `http://localhost:<port>` (any port; the
   token's ingress is overridden by our generated config when using credentials, and `PmcTunnel` always
   targets the host's actual port for token mode via the dashboard route — set the service to the port
   your game uses).

Then:

```csharp
host.TunnelMode = "named";
host.NamedTunnelToken = "<token>";        // keep it out of version control
host.NamedTunnelHostname = "party.example.com";
host.StartTunnel();
```

The join URL is built from the hostname (token mode prints no trycloudflare URL), so
`NamedTunnelHostname` is required. DNS waits, retries, `lost` handling and `pmc.moved` all behave the
same — except restarts keep the same URL, so a rolling `RestartTunnel()` never invalidates phones.

Alternative without a token (classic CLI flow):

```sh
cloudflared tunnel login
cloudflared tunnel create party          # writes ~/.cloudflared/<uuid>.json
cloudflared tunnel route dns party party.example.com
```

```csharp
host.TunnelMode = "named";
host.NamedTunnelName = "party";                  // name or UUID
host.NamedTunnelCredentialsFile = "C:/Users/you/.cloudflared/<uuid>.json";
host.NamedTunnelHostname = "party.example.com";
host.StartTunnel();
```

Named mode without a token writes `<TempDir>/named-tunnel.yml` (tunnel + credentials-file + an ingress
rule mapping the hostname to `http://127.0.0.1:<port>`) and runs `cloudflared tunnel run <name>`.

## Alternatives

| Option | Stable URL | Account | Install on host | Notes |
|---|---|---|---|---|
| Cloudflare Quick Tunnel (built-in) | no | no | auto-download | best-effort, rate limits, ~200 in-flight cap |
| Cloudflare named tunnel (built-in) | yes | yes + domain | cloudflared | stable `https://host.example.com` |
| Tailscale Funnel | yes (`*.ts.net`) | yes | Tailscale client | bandwidth limits; good for groups that play regularly |
| ngrok | paid tier | yes (authtoken) | ngrok agent | free tier's interstitial page blocks phone WebSockets until clicked |
| Hosted WS relay | yes | a service | none | works everywhere incl. Android/iOS/WebGL/console hosts; costs money to run — see relay-scope.md |
| WebRTC data channels | via signalling | a service | none | NAT traversal; TURN costs for symmetric NATs |

## Provider contract

A provider is anything `PmcHostCore` can drive through this surface (`PmcTunnel` is the concrete class):

```csharp
string State { get; }                              // "idle"|"downloading"|"starting"|"ready"|"lost"|"failed"|"stopped"
string Url { get; }                                // public base URL once ready, "" otherwise
event Action<string, string> StateChanged;         // raised on the host's Poll thread
int Start(int localPort);                          // forward http(s) traffic to 127.0.0.1:localPort
void Stop();
void Pump();                                       // the host calls it from Poll()
bool IsRunning();
```

- `ready` detail = the public URL; `failed`/`lost` detail = a human reason. `lost` means "was up, dropped,
  may recover"; `stopped` is deliberate.
- The host sets `AdvertiseUrl` to `Url` on `ready`, keeps it while `lost`, restores on `stopped`/`failed`.
- The provider must forward both HTTP and WebSocket on the same hostname/port (the join page, SDK, QR,
  `/pmc/ws` and `/pmc/healthz` all share it).
- To slot another provider in, drive the same surface — `PmcHostCore` builds the `PmcTunnel` internally,
  so a custom backend wraps `StartTunnel`/`GetTunnel` (or supplies its own object behind the same API).
