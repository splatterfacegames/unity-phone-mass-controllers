// Load test: 200 concurrent WebSocket clients against the headless Godot host (60 fps cap).
// Measures host poll time per frame and frame-time distribution, plus echo round-trip latency.
// Run: npm run test:load   (GODOT = Godot executable; CLIENTS, SECONDS env overrides)
// Exit code 1 if messages are lost or the host stops keeping up.
import WebSocket from 'ws';
import { mkdirSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { startGodotServer } from './godot-server.mjs';

const here = dirname(fileURLToPath(import.meta.url));
const CLIENTS = Number(process.env.CLIENTS || 200);
const SECONDS = Number(process.env.SECONDS || 10);
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

function pct(arr) {
  if (!arr.length) return { n: 0 };
  const s = [...arr].sort((a, b) => a - b);
  const at = (p) => s[Math.min(s.length - 1, Math.floor(s.length * p))];
  return { n: s.length, avg: s.reduce((a, b) => a + b, 0) / s.length, p50: at(0.5), p95: at(0.95), p99: at(0.99), max: s[s.length - 1] };
}

function client(port, i) {
  return new Promise((resolve, reject) => {
    const ws = new WebSocket(`ws://127.0.0.1:${port}/pmc/ws`, { perMessageDeflate: false });
    ws.stats = { sent: 0, recv: 0, rtt: [], broadcasts: 0 };
    ws.pending = new Map();
    ws.on('message', (data, isBinary) => {
      if (isBinary) return;
      const m = JSON.parse(data.toString());
      if (m.t === 'pmc.welcome') { ws.id = m.id; resolve(ws); return; }
      if (m.t !== 'msg') return;
      if (m.d && m.d.echo) {
        ws.stats.recv++;
        ws.stats.rtt.push(performance.now() - m.d.echo.ts);
      } else if (m.d && m.d.state !== undefined) {
        ws.stats.broadcasts++;
      } else if (ws.onReply) {
        ws.onReply(m.d);
      }
    });
    ws.once('open', () => ws.send(JSON.stringify({ t: 'pmc.hello', sdk: 1, name: `load${i}` })));
    ws.once('error', reject);
  });
}

function command(ws, cmd) {
  return new Promise((resolve) => {
    ws.onReply = (d) => { ws.onReply = null; resolve(d); };
    ws.send(JSON.stringify({ t: 'msg', d: cmd }));
  });
}

async function phase(name, control, clients, seconds, { hz = 0, broadcastHz = 0, payload = 40 } = {}) {
  for (const c of clients) c.stats = { sent: 0, recv: 0, rtt: [], broadcasts: 0 };
  await command(control, { cmd: 'broadcast_hz', hz: broadcastHz, bytes: 200 });
  await command(control, { cmd: 'reset_stats' });
  const pad = 'p'.repeat(payload);
  const timers = [];
  if (hz > 0) {
    for (const c of clients) {
      // Stagger start so sends are spread across the interval.
      const start = setTimeout(() => {
        timers.push(setInterval(() => {
          c.send(JSON.stringify({ t: 'msg', d: { ts: performance.now(), pad } }));
          c.stats.sent++;
        }, 1000 / hz));
      }, Math.random() * (1000 / hz));
      timers.push(start);
    }
  }
  await sleep(seconds * 1000);
  for (const t of timers) { clearInterval(t); clearTimeout(t); }
  const stats = (await command(control, { cmd: 'stats' })).stats;
  await command(control, { cmd: 'broadcast_hz', hz: 0 });
  // Drain outstanding echoes.
  const deadline = Date.now() + 5000;
  while (Date.now() < deadline && clients.some((c) => c.stats.recv < c.stats.sent)) await sleep(50);
  const sent = clients.reduce((a, c) => a + c.stats.sent, 0);
  const recv = clients.reduce((a, c) => a + c.stats.recv, 0);
  const rtt = pct(clients.flatMap((c) => c.stats.rtt));
  const broadcasts = clients.reduce((a, c) => a + c.stats.broadcasts, 0);
  const row = {
    phase: name, clients: clients.length, seconds, msgs_per_sec_in: Math.round(sent / seconds),
    sent, echoed: recv, lost: sent - recv, broadcasts_received: broadcasts,
    rtt_ms: { p50: +(rtt.p50 ?? 0).toFixed(2), p99: +(rtt.p99 ?? 0).toFixed(2), max: +(rtt.max ?? 0).toFixed(2) },
    poll_ms: pick(stats.poll), frame_ms: pick(stats.frame),
  };
  console.log(`\n[${name}]`, JSON.stringify(row, null, 1));
  return row;
}

function pick(s) {
  const r = (v) => +Number(v ?? 0).toFixed(3);
  return { frames: s.n, avg: r(s.avg_ms), p50: r(s.p50_ms), p95: r(s.p95_ms), p99: r(s.p99_ms), max: r(s.max_ms) };
}

const BUDGET = Number(process.env.BUDGET_MS || 8);
const server = await startGodotServer({ fps: 60, heartbeat: 15, budget: BUDGET });
console.log(`host io_budget_msec = ${BUDGET}`);
let failed = false;
try {
  const control = await client(server.port, 'control');
  const rows = [];
  rows.push(await phase('baseline: 1 idle client', control, [], 5));

  // Phones join over seconds, not in one burst. Godot's TCPServer has a small fixed listen backlog, so a
  // same-millisecond burst of hundreds of SYNs gets refused and retried by the OS. Stagger by STAGGER_MS.
  const stagger = Number(process.env.STAGGER_MS ?? 10);
  const t0 = performance.now();
  const clients = await Promise.all(Array.from({ length: CLIENTS }, (_, i) => sleep(i * stagger).then(() => client(server.port, i))));
  const connectMs = performance.now() - t0;
  console.log(`\nconnected + welcomed ${clients.length} clients in ${connectMs.toFixed(0)} ms (stagger ${stagger} ms)`);

  rows.push(await phase(`${CLIENTS} idle clients`, control, clients, 5));
  rows.push(await phase(`${CLIENTS} clients x 5 msg/s echo`, control, clients, SECONDS, { hz: 5 }));
  rows.push(await phase(`${CLIENTS} clients x 20 msg/s echo`, control, clients, SECONDS, { hz: 20 }));
  rows.push(await phase(`${CLIENTS} clients x 20 msg/s + 30 Hz broadcast (200 B)`, control, clients, SECONDS, { hz: 20, broadcastHz: 30 }));
  rows.push(await phase(`${CLIENTS} clients x 60 msg/s echo`, control, clients, SECONDS / 2, { hz: 60 }));

  for (const r of rows) {
    if (r.lost !== 0) { console.error(`FAIL: ${r.phase} lost ${r.lost} messages`); failed = true; }
    if (r.frame_ms.p50 > 25) { console.error(`FAIL: ${r.phase} median frame ${r.frame_ms.p50} ms (host not keeping up at 60 fps)`); failed = true; }
  }
  const result = { date: new Date().toISOString(), node: process.version, platform: `${process.platform}-${process.arch}`, connect_ms: Math.round(connectMs), rows };
  mkdirSync(join(here, 'out'), { recursive: true });
  writeFileSync(join(here, 'out', 'load.json'), JSON.stringify(result, null, 2));

  console.log('\n| phase | msgs/s in | lost | echo RTT p50 / p99 (ms) | host poll avg / p99 / max (ms) | frame p50 / p99 / max (ms) |');
  console.log('|---|---|---|---|---|---|');
  for (const r of rows) {
    console.log(`| ${r.phase} | ${r.msgs_per_sec_in} | ${r.lost} | ${r.rtt_ms.p50} / ${r.rtt_ms.p99} | ${r.poll_ms.avg} / ${r.poll_ms.p99} / ${r.poll_ms.max} | ${r.frame_ms.p50} / ${r.frame_ms.p99} / ${r.frame_ms.max} |`);
  }
  for (const c of clients) c.terminate();
  control.close();
} catch (e) {
  console.error(e);
  failed = true;
} finally {
  await server.stop();
}
process.exit(failed ? 1 : 0);
