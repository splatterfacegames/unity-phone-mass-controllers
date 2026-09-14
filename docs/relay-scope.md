# Scope: outside-LAN play on platforms that can't spawn processes

Status: **scoped, not implemented.** `PmcTunnel` needs a child `cloudflared` process
(`System.Diagnostics.Process`), so tunnels report `failed` on Android, iOS, WebGL and console builds. A
**WebGL build can't host at all** — browsers can't open listening sockets — so this is also the answer
for hosting a party from a web build (a web build would run a *relay client*, not `PmcHostCore`).

## Option A — WebSocket relay (recommended first)

A small always-on relay with a public URL. Both sides connect **out** — no listening socket, no NAT
problem, works on every platform including WebGL.

```
phone browser ──wss──▶ relay.example.com/room/<CODE>/c/<id> ◀──wss── Unity host (outbound)
```

- The host connects out to the relay, registers a room (gets a join URL like
  `https://relay.example.com/room/ABCD`), and polls `pmc.*` frames over its own outbound WebSocket.
- Phones open the join URL; the relay serves the controller page + `pmc.js` (or redirects to a static
  host) and bridges WebSocket frames phone ↔ host.
- **Wire protocol stays identical** — `pmc.hello/welcome/ping/pong/msg/...` flow over the same frames;
  the relay just copies messages between the host socket and the per-player sockets, tagging each player
  with an id the host sees as a `RemoteAddress`-equivalent. `pmc.moved`, join codes and admin auth are
  host-side concerns and keep working.
- Unity-side: a `PmcRelayProvider` implementing the provider contract from docs/tunnels.md
  (`Start(port)` → connects out instead of listening… or a relay mode on `PmcHostCore` that dials out and
  speaks the server role over the socket). The host's WebSocket implementation is engine-free
  (`System.Net.WebSockets`) and works on every platform with raw sockets; on **WebGL** a thin
  JS-interop socket is needed since browser builds can't open TCP — same trick the browser SDK already
  uses in reverse.
- Server-side: ~200-400 lines (Node `ws` or Go `nhooyr.io/websocket`): room registry, per-player sockets,
  fan-out to host, host→player routing, idle-room reaping, rate limits, optional TLS via the platform.
  Self-hostable on any free tier; a hosted option can come later.
- Effort: relay server ~1-2 days, Unity provider/host plumbing ~1-2 days, tests ~1 day. Risks: running
  costs, relay becomes the TTP for join URLs, latency adds one hop (fine for party games).

## Option B — WebRTC data channels

Phones and the host peer over `RTCDataChannel`; a tiny signalling service exchanges SDP offers/ICE.

- True P2P after setup — lowest latency, no per-message relay cost.
- Needs a WebRTC plugin (e.g. Unity's `com.unity.webrtc` package on desktop/mobile; **WebGL builds use
  the browser's RTCPeerConnection** via a JS shim — there is no built-in WebRTC on Unity Web), plus STUN
  (free) and TURN (paid, needed for symmetric NATs — some % of parties).
- The signalling service still needs a public address for the join link; phones that land on the page get
  SDP'd to the host. `pmc.*` frames map 1:1 onto the data channel.
- Effort: ~3-5 days including the JS shim and TURN ops, plus ongoing TURN cost. Higher risk, better
  latency. Consider after the relay proves demand.

## Decision

Build **Option A** first: smallest moving parts, works on every target (including browser-hosted games),
identical protocol, self-hostable. Keep `PmcTunnel` for desktop quick/named tunnels — the relay provider
slots behind the same `Start(port) → Url` contract, so `StartTunnel()` can pick whichever the
platform supports.
