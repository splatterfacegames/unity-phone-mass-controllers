// Verifies the pure-GDScript PMCQr encoder against independent implementations.
//
// 1. Runs Godot headless to dump a corpus of matrices (tests/node/qr-dump.gd).
// 2. Decodes every matrix with jsQR and checks the payload bytes match the input exactly.
// 3. Rebuilds every symbol with the `qrcode` npm package forcing the same version, EC level,
//    mode and mask, and compares module-for-module.
// 4. Decodes every matrix with ZXing (@zxing/library) as a second independent decoder.
// 5. Decodes the PNGs written by PMCQr.to_image with jsQR.
//
// Usage: node tests/node/qr-decode.mjs [--no-dump]   (GODOT env = Godot executable, default "godot")
import { createRequire } from 'node:module';
import { spawnSync } from 'node:child_process';
import { readFileSync, mkdirSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const repo = resolve(here, '..', '..');
const outDir = join(here, 'out', 'qr');
const require = createRequire(join(here, 'package.json'));
const jsQR = require('jsqr');
const QRCode = require('qrcode');
const { PNG } = require('pngjs');
const ZX = require('@zxing/library');

if (!process.argv.includes('--no-dump')) {
  mkdirSync(outDir, { recursive: true });
  const godot = process.env.GODOT || 'godot';
  const r = spawnSync(godot, ['--headless', '--path', repo, '--script', 'res://tests/node/qr-dump.gd', '--', outDir], { encoding: 'utf8', timeout: 600000 });
  process.stdout.write((r.stdout || '').split('\n').filter((l) => l.startsWith('qr-dump')).join('\n') + '\n');
  if (r.status !== 0) {
    console.error(r.stdout, r.stderr);
    console.error(`qr-dump failed (exit ${r.status})`);
    process.exit(1);
  }
}

const corpus = JSON.parse(readFileSync(join(outDir, 'corpus.json'), 'utf8'));
const entries = corpus.entries;
const LEVELS = ['L', 'M', 'Q', 'H'];
let failures = 0;
const fail = (msg) => { failures++; if (failures <= 40) console.error('FAIL ' + msg); };
const stats = { zxing: 0, jsqrLimit: [],jsqr: 0, qrcode: 0, qrcodeSkipped: 0, png: 0, versions: new Set(), levels: new Set(), modes: new Set() };

function render(rows, scale = 4, quiet = 4) {
  const n = rows.length;
  const w = (n + quiet * 2) * scale;
  const data = new Uint8ClampedArray(w * w * 4).fill(255);
  for (let y = 0; y < n; y++) for (let x = 0; x < n; x++) {
    if (rows[y][x] !== '1') continue;
    for (let dy = 0; dy < scale; dy++) for (let dx = 0; dx < scale; dx++) {
      const o = (((y + quiet) * scale + dy) * w + (x + quiet) * scale + dx) * 4;
      data[o] = data[o + 1] = data[o + 2] = 0;
    }
  }
  return { data, w };
}

function zxingDecode(rows, scale = 4, quiet = 4) {
  const n = rows.length;
  const w = (n + quiet * 2) * scale;
  const lum = new Uint8ClampedArray(w * w).fill(255);
  for (let y = 0; y < n; y++) for (let x = 0; x < n; x++) {
    if (rows[y][x] !== '1') continue;
    for (let dy = 0; dy < scale; dy++) for (let dx = 0; dx < scale; dx++) lum[((y + quiet) * scale + dy) * w + (x + quiet) * scale + dx] = 0;
  }
  const bmp = new ZX.BinaryBitmap(new ZX.HybridBinarizer(new ZX.RGBLuminanceSource(lum, w, w)));
  const hints = new Map([[ZX.DecodeHintType.PURE_BARCODE, true], [ZX.DecodeHintType.CHARACTER_SET, 'UTF-8']]);
  return new ZX.QRCodeReader().decode(bmp, hints);
}

function checkDecode(quietFail, label, e, data, w) {
  const res = jsQR(data, w, w, { inversionAttempts: 'dontInvert' });
  if (!res) return quietFail ? false : fail(`${label}: jsQR could not decode (${e.name} v${e.version} ${e.level} ${e.mode})`);
  const got = Buffer.from(res.binaryData);
  const want = Buffer.from(e.text, 'utf8');
  if (!got.equals(want)) return fail(`${label}: payload mismatch for ${e.name}: got ${JSON.stringify(got.toString('utf8').slice(0, 60))}`);
  if (res.version !== e.version) return fail(`${label}: jsQR version ${res.version} != ${e.version} (${e.name})`);
  return true;
}

for (const [i, e] of entries.entries()) {
  stats.versions.add(e.version); stats.levels.add(e.level); stats.modes.add(e.mode);
  if (e.rows.length !== e.size || e.size !== 17 + 4 * e.version) fail(`#${i} bad size`);
  const { data, w } = render(e.rows);
  if (checkDecode(true, `#${i} matrix`, e, data, w) === true) stats.jsqr++;
  else stats.jsqrLimit.push(i);
  let zxOk = false;
  try {
    const r = zxingDecode(e.rows);
    if (r.getText() === e.text) { zxOk = true; stats.zxing++; } else fail(`#${i} ZXing payload mismatch for ${e.name}`);
  } catch (err) { fail(`#${i} ZXing could not decode ${e.name} v${e.version} ${e.level} ${e.mode}: ${err.constructor.name}`); }
  e._zx = zxOk;

  // Module-for-module comparison with the qrcode package (same version/level/mode/mask).
  if (e.text.length === 0) { stats.qrcodeSkipped++; continue; }
  let ref;
  try {
    const segData = e.mode === 'byte' ? new Uint8Array(Buffer.from(e.text, 'utf8')) : e.text;
    ref = QRCode.create([{ data: segData, mode: e.mode }], { version: e.version, errorCorrectionLevel: LEVELS[e.ecc], maskPattern: e.mask });
  } catch (err) {
    fail(`#${i} qrcode threw for ${e.name}: ${err.message}`);
    continue;
  }
  const m = ref.modules;
  if (m.size !== e.size) { fail(`#${i} qrcode size ${m.size} != ${e.size}`); continue; }
  let diff = 0;
  for (let y = 0; y < m.size; y++) for (let x = 0; x < m.size; x++) {
    if ((m.get(y, x) ? '1' : '0') !== e.rows[y][x]) diff++;
  }
  if (diff) fail(`#${i} ${e.name} v${e.version} ${e.level} ${e.mode} mask${e.mask}: ${diff} modules differ from qrcode`);
  else { stats.qrcode++; e._ref = true; }
}
// jsQR has a few blind spots on large symbols; accept a jsQR miss only when ZXing decodes the symbol
// exactly and it is module-identical to the qrcode package.
for (const i of stats.jsqrLimit) {
  const e = entries[i];
  if (!(e._zx && e._ref)) fail(`#${i} jsQR could not decode ${e.name} (not confirmed by ZXing + qrcode)`);
}

for (const e of entries.filter((x) => x.png)) {
  const png = PNG.sync.read(readFileSync(join(outDir, e.png)));
  const expectW = (e.size + 8) * e.png_module_px;
  if (png.width !== expectW || png.height !== expectW) { fail(`${e.png}: size ${png.width} != ${expectW}`); continue; }
  if (checkDecode(false, `png ${e.png}`, e, new Uint8ClampedArray(png.data), png.width) === true) stats.png++;
}

console.log(`qr-decode: ${entries.length} matrices; ZXing exact ${stats.zxing}/${entries.length}; jsQR exact ${stats.jsqr}/${entries.length}` +
  (stats.jsqrLimit.length ? ` (jsQR misses, confirmed by ZXing+qrcode: ${stats.jsqrLimit.map((i) => entries[i].name).join(', ')})` : '') + '; ' +
  `qrcode module-identical ${stats.qrcode}/${entries.length - stats.qrcodeSkipped} (${stats.qrcodeSkipped} skipped: empty text); ` +
  `PNG decoded ${stats.png}/${entries.filter((x) => x.png).length}; versions ${Math.min(...stats.versions)}-${Math.max(...stats.versions)} (${stats.versions.size} distinct); ` +
  `levels ${[...stats.levels].join('')}; modes ${[...stats.modes].join(',')}`);
console.log(`qr-decode: typical URL encode ${corpus.typical_url_ms.toFixed(2)} ms, tunnel URL ${corpus.tunnel_url_ms.toFixed(2)} ms (Godot headless)`);
if (failures) { console.error(`qr-decode: ${failures} failure(s)`); process.exit(1); }
console.log('qr-decode: OK');
