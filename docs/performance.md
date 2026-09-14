# Performance

How the host spends frame time, what the measured limits are, and the knobs that move them.

## The I/O model

A dedicated worker thread owns accept, `read`/`write`, and HTTP/WS frame decode. The game thread
drains a queue of complete events — accepted sockets, parsed HTTP requests, decoded WS frames —
inside `host.Poll()`, applies them, and queues outbound frames back through a per-connection lock.
Game code, events, `Send`, `Broadcast`, routes and timers all stay on the calling thread.

- `IoBudgetMsec` (default 8) bounds how long `Poll()` spends draining the event queue per call.
  Undrained events keep their per-connection order and are handled next call.
- Cross-thread handoff adds a little latency to ping/pong and write flushes (one worker pass, ~1 ms
  when idle) in exchange for taking the syscalls and frame decoding off the frame budget.
- Per-player `RttMs` still works (the samples include the worker hop, so they read a few ms higher
  under load).
- Worker limits are snapshots taken at `Start()`: `MaxHeaderBytes`, `MaxBodyBytes`,
  `MaxMessageBytes`. Per-connection output (`Send`, `Broadcast`, responses) is mutexed; everything
  else on a connection is single-owner.
- HTTP requests are serialized per connection: the worker holds off parsing the next request until
  the previous response is queued, so a pipelined head can never overtake a streamed file body.

## Measured numbers

`PmcHarness` (net8.0, Release) on Windows 11, loopback, host at 60 fps, 200 Node `ws` clients joining
10 ms apart and sending `{"t":"msg","d":{...}}` which the host echoes. Reproduce with
`cd tests/node && npm run test:load`.

| phase | msgs/s in | lost | echo RTT p50 / p99 | host poll avg / p99 | frame p50 |
|---|---|---|---|---|---|
| baseline: 1 idle client | 0 | 0 | – | 0.01 / 0.14 ms | 16.67 ms |
| 200 idle clients | 0 | 0 | – | 0.02 / 0.17 ms | 16.67 ms |
| 200 × 5 msg/s echo | ~930 | 0 | 12.6 / 33.6 ms | 0.20 / 1.81 ms | 16.67 ms |
| 200 × 20 msg/s echo | ~3,300 | 0 | 33.8 / 151.4 ms | 2.01 / 23.8 ms | 21.8 ms |
| + 30 Hz broadcast (200 B) | ~3,500 | 0 | 23.0 / 110.8 ms | 1.25 / 18.5 ms | 16.67 ms |
| 200 × 60 msg/s echo | ~10,400 | 0 | 17.4 / 64.7 ms | 1.68 / 9.94 ms | 16.67 ms |

No messages were lost in any phase and the median frame held 60 fps in all but the mid-load phase
(p99 poll spikes under a 3.3k msgs/s burst ate into a few frames; the loop recovered each time).

> Note for the Godot comparison: these are the .NET harness's numbers, not the original addon's —
> GDScript poll costs were ~5–10× higher at the same load. The workload shape (200 clients, echo +
> broadcast mix) matches `tests/node/load.test.mjs` so the tables are comparable.

## Known limits

- **Burst joins are bounded by the OS listen backlog.** 200 clients connecting 10 ms apart took
  ~6.4 s to all get welcomed; with 30 ms spacing every client connected on schedule. The OS retries
  refused connections, so this is slow, not broken. The I/O worker accepts up to 64 sockets per pass
  and drains bursts as fast as the backlog allows.
- **Frame pacing needs a fine sleep resolution.** On Windows the harness calls `timeBeginPeriod(1)` —
  the same trick Godot uses — or `Thread.Sleep` rounds to ~15.6 ms and a 60 fps loop idles at ~40 fps.
  In Unity the engine's frame clock handles this for you; only standalone `Poll()` drivers need it.
- **Big payloads hurt in one place: JSON.** `JObject.Parse` runs inside `Poll()` — a multi-MiB text
  message stalls the whole drain. Prefer binary frames for large data (`MessageReceived` delivers
  `byte[]`), or parse off the game thread.
- `MaxBacklogBytes` (16 MiB) drops sockets that can't keep up; `IoBudgetMsec` trades latency for
  frame time under load.
