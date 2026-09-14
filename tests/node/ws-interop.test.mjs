// RFC 6455 interop between the Godot host (pure GDScript server) and the Node `ws` client,
// plus raw-socket protocol violations. Run: npm test (GODOT = Godot executable).
import { test, before, after } from 'node:test';
import assert from 'node:assert/strict';
import net from 'node:net';
import crypto from 'node:crypto';
import WebSocket from 'ws';
import { startGodotServer } from './godot-server.mjs';

let server;
let url;

before(async () => {
  server = await startGodotServer({ heartbeat: 0.3, grace: 1 });
  url = `ws://127.0.0.1:${server.port}/pmc/ws`;
});

after(async () => {
  if (server) await server.stop();
});

// ---------------------------------------------------------------------------------------------
// helpers

function open(opts = {}) {
  return new Promise((resolve, reject) => {
    const ws = new WebSocket(url, { maxPayload: 64 * 1024 * 1024, ...opts });
    ws.inbox = [];
    ws.waiters = [];
    ws.on('message', (data, isBinary) => {
      const item = isBinary ? { binary: true, data } : { binary: false, json: JSON.parse(data.toString()) };
      ws.inbox.push(item);
      for (const w of [...ws.waiters]) w();
    });
    ws.once('open', () => resolve(ws));
    ws.once('error', reject);
  });
}

function next(ws, pred, timeout = 5000) {
  return new Promise((resolve, reject) => {
    const check = () => {
      const i = ws.inbox.findIndex(pred);
      if (i >= 0) {
        const [item] = ws.inbox.splice(i, 1);
        ws.waiters = ws.waiters.filter((w) => w !== check);
        clearTimeout(timer);
        resolve(item);
        return true;
      }
      return false;
    };
    const timer = setTimeout(() => {
      ws.waiters = ws.waiters.filter((w) => w !== check);
      reject(new Error('timeout waiting for message'));
    }, timeout);
    if (!check()) ws.waiters.push(check);
  });
}

const nextJson = (ws, t, timeout) => next(ws, (m) => !m.binary && m.json.t === t, timeout).then((m) => m.json);
const nextBinary = (ws, timeout) => next(ws, (m) => m.binary, timeout).then((m) => m.data);

function closed(ws, timeout = 5000) {
  return new Promise((resolve, reject) => {
    if (ws.readyState === WebSocket.CLOSED) return resolve({ code: ws._closeCode, reason: '' });
    const timer = setTimeout(() => reject(new Error('timeout waiting for close')), timeout);
    ws.once('close', (code, reason) => { clearTimeout(timer); resolve({ code, reason: reason.toString() }); });
  });
}

async function joined(extra = {}) {
  const ws = await open();
  ws.send(JSON.stringify({ t: 'pmc.hello', sdk: 1, ...extra }));
  const welcome = await nextJson(ws, 'pmc.welcome');
  return { ws, welcome };
}

// Raw socket client that speaks just enough WebSocket to misbehave.
function rawOpen() {
  return new Promise((resolve, reject) => {
    const sock = net.connect(server.port, '127.0.0.1');
    const key = crypto.randomBytes(16).toString('base64');
    let buf = Buffer.alloc(0);
    let upgraded = false;
    sock.frames = [];
    sock.closedTcp = false;
    sock.on('close', () => { sock.closedTcp = true; });
    sock.on('error', () => {});
    sock.on('data', (chunk) => {
      buf = Buffer.concat([buf, chunk]);
      if (!upgraded) {
        const end = buf.indexOf('\r\n\r\n');
        if (end < 0) return;
        const head = buf.subarray(0, end).toString();
        buf = buf.subarray(end + 4);
        if (!head.startsWith('HTTP/1.1 101')) return reject(new Error('upgrade failed: ' + head));
        const accept = crypto.createHash('sha1').update(key + '258EAFA5-E914-47DA-95CA-C5AB0DC85B11').digest('base64');
        if (!head.includes(`Sec-WebSocket-Accept: ${accept}`)) return reject(new Error('bad accept'));
        upgraded = true;
        resolve(sock);
      }
      // Parse server (unmasked) frames.
      while (buf.length >= 2) {
        let len = buf[1] & 0x7f;
        let off = 2;
        if (len === 126) { if (buf.length < 4) break; len = buf.readUInt16BE(2); off = 4; }
        else if (len === 127) { if (buf.length < 10) break; len = Number(buf.readBigUInt64BE(2)); off = 10; }
        if (buf.length < off + len) break;
        sock.frames.push({ fin: !!(buf[0] & 0x80), op: buf[0] & 0x0f, payload: buf.subarray(off, off + len) });
        buf = buf.subarray(off + len);
      }
    });
    sock.write(`GET /pmc/ws HTTP/1.1\r\nHost: 127.0.0.1\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: ${key}\r\nSec-WebSocket-Version: 13\r\n\r\n`);
  });
}

function frame(op, payload, { fin = true, mask = true, rsv = 0 } = {}) {
  payload = Buffer.from(payload);
  const n = payload.length;
  const head = [(fin ? 0x80 : 0) | rsv | op];
  const m = mask ? 0x80 : 0;
  let ext = Buffer.alloc(0);
  if (n < 126) head.push(m | n);
  else if (n < 65536) { head.push(m | 126); ext = Buffer.alloc(2); ext.writeUInt16BE(n); }
  else { head.push(m | 127); ext = Buffer.alloc(8); ext.writeBigUInt64BE(BigInt(n)); }
  if (!mask) return Buffer.concat([Buffer.from(head), ext, payload]);
  const key = crypto.randomBytes(4);
  const masked = Buffer.alloc(n);
  for (let i = 0; i < n; i++) masked[i] = payload[i] ^ key[i & 3];
  return Buffer.concat([Buffer.from(head), ext, key, masked]);
}

function waitFor(cond, timeout = 5000) {
  return new Promise((resolve, reject) => {
    const start = Date.now();
    const tick = () => {
      const v = cond();
      if (v) return resolve(v);
      if (Date.now() - start > timeout) return reject(new Error('timeout'));
      setTimeout(tick, 10);
    };
    tick();
  });
}

async function rawCloseCode(sock) {
  const f = await waitFor(() => sock.frames.find((fr) => fr.op === 8));
  return f.payload.length >= 2 ? f.payload.readUInt16BE(0) : 1005;
}

async function rawHello(sock) {
  sock.write(frame(1, JSON.stringify({ t: 'pmc.hello', sdk: 1 })));
  await waitFor(() => sock.frames.find((fr) => fr.op === 1 && fr.payload.toString().includes('pmc.welcome')));
}

function httpGet(path) {
  return new Promise((resolve, reject) => {
    const sock = net.connect(server.port, '127.0.0.1');
    let data = '';
    sock.on('data', (c) => { data += c.toString(); });
    sock.on('end', () => resolve(data));
    sock.on('error', reject);
    sock.write(`GET ${path} HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n`);
  });
}

// ---------------------------------------------------------------------------------------------
// ws client interop

test('hello, welcome, text and binary echo', async () => {
  const { ws, welcome } = await joined({ name: 'node' });
  assert.equal(welcome.name, 'node');
  assert.equal(typeof welcome.id, 'number');
  assert.match(welcome.token, /^[0-9a-f]{32}$/);
  ws.send(JSON.stringify({ t: 'msg', d: { a: [1, 2, 3], s: 'ünïcödé ✓' } }));
  assert.deepEqual((await nextJson(ws, 'msg')).d, { echo: { a: [1, 2, 3], s: 'ünïcödé ✓' } });
  const bin = crypto.randomBytes(777);
  ws.send(bin);
  assert.deepEqual(await nextBinary(ws), bin);
  ws.close();
  await closed(ws);
});

test('1 MiB binary and ~1 MiB text messages', async () => {
  const { ws } = await joined();
  const big = crypto.randomBytes(1024 * 1024);
  ws.send(big);
  const back = await nextBinary(ws, 20000);
  assert.equal(back.length, big.length);
  assert.ok(back.equals(big), '1 MiB binary intact');
  const text = 'y'.repeat(1024 * 1024 - 64);
  ws.send(JSON.stringify({ t: 'msg', d: text }));
  // The echo wraps d in {"echo": ...}, which pushes it slightly over 1 MiB; the host doesn't limit outbound size.
  const m = await nextJson(ws, 'msg', 20000);
  assert.equal(m.d.echo, text);
  ws.close();
  await closed(ws);
});

test('fragmented messages (client-side fragmentation)', async () => {
  const { ws } = await joined();
  const msg = Buffer.from(JSON.stringify({ t: 'msg', d: 'frag ✓ mented' }));
  // Split inside the multi-byte check mark.
  const cut = msg.indexOf(Buffer.from('✓')) + 1;
  ws.send(msg.subarray(0, 4), { binary: false, fin: false });
  ws.ping(Buffer.from('mid'));
  ws.send(msg.subarray(4, cut), { binary: false, fin: false });
  ws.send(msg.subarray(cut), { binary: false, fin: true });
  assert.equal((await nextJson(ws, 'msg')).d.echo, 'frag ✓ mented');
  const parts = [crypto.randomBytes(100000), crypto.randomBytes(1), crypto.randomBytes(70000)];
  parts.forEach((p, i) => ws.send(p, { binary: true, fin: i === parts.length - 1 }));
  assert.ok((await nextBinary(ws)).equals(Buffer.concat(parts)));
  ws.close();
  await closed(ws);
});

test('ping/pong both directions and heartbeat keeps a live socket', async () => {
  const { ws } = await joined();
  const pong = new Promise((r) => ws.once('pong', (d) => r(d.toString())));
  ws.ping(Buffer.from('hello-ping'));
  assert.equal(await pong, 'hello-ping');
  let serverPings = 0;
  ws.on('ping', () => { serverPings++; });
  await new Promise((r) => setTimeout(r, 1500)); // heartbeat is 0.3 s; ws auto-pongs
  assert.ok(serverPings >= 3, `server pinged (${serverPings})`);
  assert.equal(ws.readyState, WebSocket.OPEN);
  ws.send(JSON.stringify({ t: 'msg', d: 'still here' }));
  assert.equal((await nextJson(ws, 'msg')).d.echo, 'still here');
  ws.close();
  await closed(ws);
});

test('dead peer (no pongs) is closed by the heartbeat', async () => {
  const ws = await open({ autoPong: false });
  ws.send(JSON.stringify({ t: 'pmc.hello', sdk: 1 }));
  await nextJson(ws, 'pmc.welcome');
  const t0 = Date.now();
  const c = await closed(ws, 5000);
  assert.ok(Date.now() - t0 < 2500, `closed after ${Date.now() - t0} ms`);
  assert.ok(c.code === 1006 || c.code === 1005 || c.code === 1001, `close code ${c.code}`);
});

test('close codes: echo of client code, reject 4000, oversize 1009', async () => {
  const { ws } = await joined();
  ws.close(4321, 'custom');
  assert.equal((await closed(ws)).code, 4321);

  const r = await open();
  r.send(JSON.stringify({ t: 'pmc.hello', sdk: 99 }));
  const rej = await nextJson(r, 'pmc.reject');
  assert.equal(rej.code, 'version');
  assert.equal((await closed(r)).code, 4000);

  const o = await joined();
  o.ws.send(crypto.randomBytes(1024 * 1024 + 1));
  assert.equal((await closed(o.ws, 10000)).code, 1009);
});

test('abrupt terminate does not disturb the host', async () => {
  for (let i = 0; i < 20; i++) {
    const { ws } = await joined();
    ws.send(crypto.randomBytes(50000));
    ws.terminate();
  }
  const res = await httpGet('/pmc/healthz');
  assert.match(res, /^HTTP\/1\.1 200/);
});

// ---------------------------------------------------------------------------------------------
// raw protocol violations

const violations = [
  ['unmasked client frame', () => frame(1, '{"t":"msg","d":1}', { mask: false }), 1002],
  ['RSV1 set without extension', () => frame(1, 'x', { rsv: 0x40 }), 1002],
  ['RSV3 set', () => frame(2, 'x', { rsv: 0x10 }), 1002],
  ['reserved opcode 0x3', () => frame(3, ''), 1002],
  ['reserved control opcode 0xB', () => frame(0xb, ''), 1002],
  ['continuation without start', () => frame(0, 'x'), 1002],
  ['text while fragmented', () => Buffer.concat([frame(1, 'a', { fin: false }), frame(1, 'b')]), 1002],
  ['fragmented ping', () => frame(9, '', { fin: false }), 1002],
  ['ping payload > 125', () => frame(9, Buffer.alloc(126)), 1002],
  ['invalid UTF-8 text', () => frame(1, Buffer.from([0xed, 0xa0, 0x80])), 1007],
  ['invalid UTF-8 across fragments', () => Buffer.concat([frame(1, Buffer.from([0xe2, 0x82]), { fin: false }), frame(0, Buffer.from([0x28]))]), 1007],
  ['close code 1004', () => frame(8, Buffer.from([0x03, 0xec])), 1002],
  ['close code 5000', () => frame(8, Buffer.from([0x13, 0x88])), 1002],
  ['close one-byte payload', () => frame(8, Buffer.from([0x03])), 1002],
  ['close reason invalid UTF-8', () => frame(8, Buffer.from([0x03, 0xe8, 0xff])), 1007],
  ['fragmented total over max', () => Buffer.concat([frame(2, Buffer.alloc(700000), { fin: false }), frame(0, Buffer.alloc(400000))]), 1009],
];

for (const [name, make, code] of violations) {
  test(`violation: ${name} -> ${code}`, async () => {
    const sock = await rawOpen();
    await rawHello(sock);
    sock.write(make());
    assert.equal(await rawCloseCode(sock), code);
    await waitFor(() => sock.closedTcp, 3000);
    sock.destroy();
  });
}

test('violation: 2^63-ish declared length -> 1002/1009 before payload', async () => {
  const sock = await rawOpen();
  await rawHello(sock);
  sock.write(Buffer.from([0x82, 0xff, 0x7f, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 1, 2, 3, 4]));
  const code = await rawCloseCode(sock);
  assert.ok(code === 1009 || code === 1002, `code ${code}`);
  sock.destroy();
});

test('raw TCP garbage, partial headers and partial frames do not crash the host', async () => {
  const garbage = [
    crypto.randomBytes(8192),
    Buffer.from('GET /pmc/ws HTTP/1.1\r\nUpgrade: websocket\r\n'),
    Buffer.from('\x00\x01\x02\r\n\r\n'),
    Buffer.from('GET / HTTP/1.1\r\n' + 'X: y\r\n'.repeat(5000)),
  ];
  for (const g of garbage) {
    const s = net.connect(server.port, '127.0.0.1');
    s.on('error', () => {});
    await new Promise((r) => s.once('connect', r));
    s.write(g);
    await new Promise((r) => setTimeout(r, 50));
    s.destroy();
  }
  for (let i = 0; i < 10; i++) {
    const sock = await rawOpen();
    await rawHello(sock);
    const f = frame(2, crypto.randomBytes(5000));
    sock.write(f.subarray(0, 1 + (i * 97) % (f.length - 1))); // truncated frame
    sock.destroy();
  }
  const junkWs = await rawOpen();
  junkWs.write(crypto.randomBytes(4096));
  await waitFor(() => junkWs.closedTcp, 5000);
  const res = await httpGet('/pmc/healthz');
  assert.match(res, /^HTTP\/1\.1 200/);
  const { ws, welcome } = await joined();
  assert.ok(welcome.id > 0);
  ws.close();
  await closed(ws);
});
