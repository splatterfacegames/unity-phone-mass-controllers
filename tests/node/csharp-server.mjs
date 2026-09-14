// Spawns the headless C# echo host (tests/harness, PmcHostCore) and resolves once it listens.
// Same contract as the Godot harness it replaces: prints "PMC_READY port=<n>".
import { spawn, execFileSync } from 'node:child_process';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { existsSync } from 'node:fs';

const here = dirname(fileURLToPath(import.meta.url));
export const repo = resolve(here, '..', '..');
const csproj = resolve(here, '..', 'harness', 'PmcHarness.csproj');
const dll = resolve(here, '..', 'harness', 'bin', 'Release', 'net8.0', 'PmcHarness.dll');

let buildPromise = null;
function build() {
  if (!buildPromise) {
    buildPromise = Promise.resolve().then(() => {
      execFileSync('dotnet', ['build', csproj, '-c', 'Release', '--nologo', '-v', 'q'], { stdio: 'inherit' });
      if (!existsSync(dll)) throw new Error('harness build produced no ' + dll);
    });
  }
  return buildPromise;
}

/**
 * @param {Record<string,string|number>} args e.g. { heartbeat: 0.3 }
 * @returns {Promise<{port:number, proc:import('node:child_process').ChildProcess, output:()=>string, stop:()=>Promise<void>}>}
 */
export async function startCSharpServer(args = {}) {
  await build();
  const argv = [dll];
  for (const [k, v] of Object.entries(args)) argv.push(`--${k}=${v}`);
  const proc = spawn('dotnet', argv, { stdio: ['ignore', 'pipe', 'pipe'] });
  let out = '';
  return new Promise((resolvePromise, reject) => {
    const timer = setTimeout(() => {
      proc.kill();
      reject(new Error('C# harness did not start in 60 s. Output:\n' + out));
    }, 60000);
    const onData = (chunk) => {
      out += chunk.toString();
      const m = out.match(/PMC_READY port=(\d+)/);
      if (m) {
        clearTimeout(timer);
        proc.stdout.off('data', onData);
        proc.stdout.on('data', (c) => { out += c.toString(); });
        resolvePromise({
          port: Number(m[1]),
          proc,
          output: () => out,
          stop: () => new Promise((r) => {
            if (proc.exitCode !== null) return r();
            proc.once('exit', () => r());
            proc.kill();
          }),
        });
      }
    };
    proc.stdout.on('data', onData);
    proc.stderr.on('data', (c) => { out += c.toString(); });
    proc.once('error', (e) => { clearTimeout(timer); reject(e); });
    proc.once('exit', (code) => {
      clearTimeout(timer);
      reject(new Error(`PmcHarness exited early (code ${code}). Output:\n${out}`));
    });
  });
}
