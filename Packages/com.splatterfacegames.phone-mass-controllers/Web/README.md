# pmc.js: controller SDK

`pmc.js` is a zero-dependency ES module for phone controller pages. `PmcHost` serves it at
`/pmc/pmc.js`, with types in `pmc.d.ts`. You don't need a build step: import it from any page the host serves.

```html
<script type="module">
  import { connect, feedback, keepScreenOn } from '/pmc/pmc.js';

  const pmc = connect({ name: 'Sam', profile: { color: '#3d8bfd' } });
  pmc.on('welcome', ({ id, rejoined }) => console.log('I am player', id, rejoined ? '(rejoined)' : ''));
  pmc.on('message', (d) => { /* JSON from Host.Send()/Broadcast() */ });
  pmc.on('binary', (buf) => { /* ArrayBuffer from Host.Send(player, byte[]) */ });
  pmc.on('status', (s) => { /* 'connecting' | 'open' | 'reconnecting' | 'closed' */ });
  pmc.on('moved', ({ url }) => { /* join URL changed (ephemeral tunnel): ask for a re-scan */ });

  keepScreenOn(); // keep the phone awake while playing
  button.onpointerdown = () => { pmc.send({ type: 'buzz', at: pmc.timestamp() }); feedback('buzz'); };
</script>
```

## API

| | |
|---|---|
| `connect(opts?)` → `PMCClient` | Opens the connection on the next microtask, so handlers attached right away catch the first events. |
| `opts.name`, `opts.profile` | Identity sent in `pmc.hello`. It's kept for every reconnect. |
| `opts.code` | Join code. Defaults to `?code=` in the page URL. |
| `opts.url` | WebSocket URL. Defaults to `ws://<host>/pmc/ws`, or `wss://` when the page is https (e.g. behind the tunnel). |
| `opts.tokenKey` | localStorage key for the rejoin token. Defaults to `pmc.token:<host:port>`. |
| `pmc.on(event, fn)` / `pmc.off(event, fn)` | Events: `welcome`, `message`, `binary`, `status`, `reject` (`{code, reason}`), `kicked` (reason), `replaced`, `auth` (bool), `moved` (`{url}`). |
| `pmc.send(data)` → bool | JSON game message. Returns `false` and drops the message when not connected (nothing is queued). |
| `pmc.sendBinary(bufOrView)` → bool | Raw binary frame, which the host receives untouched as a `byte[]`. |
| `pmc.auth(pin)` → `Promise<boolean>` | Admin elevation. The host locks a connection out for 30 s after 5 failures. |
| `pmc.setProfile({name?, profile?})` | Updates identity now and for later hellos. |
| `pmc.leave()` | Explicit leave with no grace period. It stops reconnecting but keeps the token. |
| `pmc.reconnect()` | Starts again after the client stopped (e.g. after the user fixes a bad join code via `pmc.code = ...`). |
| `pmc.serverNow()` | Host clock in ms (epoch ms). The offset is the median of the last 5 ping/pong samples. |
| `pmc.timestamp()` | Same clock as `serverNow()` — stamp inputs with it (`{type:'buzz', at: pmc.timestamp()}`) so the host can order them fairly. |
| `pmc.rttMs` | Rolling average round-trip time in ms from ping/pong (0 until the first pong). |
| `pmc.id`, `pmc.admin`, `pmc.status`, `pmc.name`, `pmc.profile`, `pmc.joinUrl`, `pmc.token` | Current state. |
| `vibrate(pattern)` → bool | Feature-detected `navigator.vibrate` (absent on iOS). |
| `feedback(kind)` → `'vibrate'`/`'flash'`/`false` | `kind` is `'buzz'`/`'success'`/`'error'`. Vibrates where supported; otherwise a 60 ms screen flash plus a short WebAudio click (audio only after a user gesture). |
| `wakeLock()` → `Promise<boolean>` | Raw screen wake lock. Needs a secure context (https or localhost); re-acquired on visibility. |
| `keepScreenOn()` → `Promise<boolean>` | Wake lock where allowed; on insecure `http://` LAN pages it falls back to a muted looping clip that starts on the next user gesture (NoSleep-style). |

## Behaviour

- **Rejoin:** the token from `pmc.welcome` goes into localStorage (and is mirrored to a
  `pmc_token` cookie with `SameSite=Strict`, which gated custom routes can check). A reload or reconnect
  sends it back, and the host resumes the same player (same id and meta) within `GraceSeconds`, or later via `RememberSeconds`.
- **One token per origin:** two tabs in one browser profile share the localStorage token, so they count as the
  same player — the newer tab wins and the older one gets `replaced`. For per-tab identities while developing,
  pass a different `opts.tokenKey` per tab (or use separate browser profiles / incognito windows).
- **Reconnect:** uses exponential backoff from 250 ms with "equal jitter", capped at 5 s. It also retries straight away when
  the page becomes visible again, because mobile browsers suspend background sockets.
- **Terminal states:** after `reject`, `kicked` or `replaced` (close codes 4000/4001/4002), the client never retries on its own.
- **Liveness:** the client sends `pmc.ping` every 5 s. If no pong arrives for 16 s, it treats the socket as dead and reconnects.
  If the host doesn't answer a hello within 10 s, the attempt is dropped and retried.
- **Moved (`pmc.moved`):** when the host announces a new join URL (an ephemeral tunnel moved), the page auto-follows
  only for https→https after a reachability check; otherwise it emits `moved` so your page can show "re-scan the QR".
  A LAN (`http://`) page never navigates itself.
- **Clock sync:** `serverNow()`/`timestamp()` estimate the host clock from ping/pong; `rttMs` is the rolling RTT.
  For timing games, stamp inputs with `timestamp()` and let the host credit them bounded by each player's RTT —
  that keeps remote (tunnelled) players competitive with local ones.

## Platform notes

- iOS Safari (and every iOS browser — they're all WebKit) has no `navigator.vibrate`, so `vibrate()` returns
  `false` there. Use `feedback()` for a visual/audio fallback.
- Wake lock needs a secure context. On a plain `http://192.168.x.x` LAN page it's unavailable, so
  `keepScreenOn()` plays a hidden looping clip after the first tap instead. Over the https tunnel the real API works.
- Backgrounded tabs and locked screens freeze or kill WebSockets, especially on iOS and Android battery savers.
  The client recovers when the page comes back, and the host keeps the player for `GraceSeconds` —
  60–120 s is a good value for pocketed phones. Pair it with `keepScreenOn()` so the screen doesn't sleep mid-game.
- See `docs/mobile-browsers.md` in the repository for the full list of phone-browser caveats.
