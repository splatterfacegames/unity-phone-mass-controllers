// Spawns the headless Godot echo host (res://tests/lib/pmc_test_server.gd) and resolves once it listens.
import { spawn } from 'node:child_process';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
export const repo = resolve(here, '..', '..');

/**
 * @param {Record<string,string|number>} args e.g. { heartbeat: 0.3 }
 * @returns {Promise<{port:number, proc:import('node:child_process').ChildProcess, output:()=>string, stop:()=>Promise<void>}>}
 */
export function startGodotServer(args = {}) {
  const godot = process.env.GODOT || 'godot';
  const argv = ['--headless', '--path', repo, '--script', 'res://tests/lib/pmc_test_server.gd', '--'];
  for (const [k, v] of Object.entries(args)) argv.push(`--${k}=${v}`);
  const proc = spawn(godot, argv, { stdio: ['ignore', 'pipe', 'pipe'] });
  let out = '';
  return new Promise((resolvePromise, reject) => {
    const timer = setTimeout(() => {
      proc.kill();
      reject(new Error('Godot server did not start in 60 s. Output:\n' + out));
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
      reject(new Error(`Godot exited early (code ${code}). Output:\n${out}`));
    });
  });
}
