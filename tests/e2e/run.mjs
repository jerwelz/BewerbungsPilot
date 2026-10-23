// Runs the browser suites. Every suite gets its own app instance on port 5299 with a fresh,
// throwaway data directory (.data/<suite>-<time>) – the real storage/ folder is never used.
//
// Usage (from tests/e2e):  npm install   then   node run.mjs            → all suites
//                                                 node run.mjs m4 m9     → selected suites
// The app is built once into .build/ (not bin/), so a running copy of the app is not disturbed.
import { spawn, spawnSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';

const DIR = path.dirname(new URL(import.meta.url).pathname.replace(/^\/([A-Z]:)/, '$1'));
const PROJECT = path.resolve(DIR, '../../src/BewerbungsPilot');
const BUILD = path.join(DIR, '.build');
const BASE = 'http://localhost:5299';

// Extra app settings per suite (everything else uses the defaults below).
const SUITE_SETTINGS = {
  'm9-expiry': ['--Auth:SessionLifetime', '00:00:15'],
  m10: ['--Demo:Enabled', 'true'],
};

const all = fs.readdirSync(DIR).filter((f) => /^m\d[\w-]*\.mjs$/.test(f)).map((f) => f.replace('.mjs', ''))
  .sort((a, b) => parseInt(a.slice(1)) - parseInt(b.slice(1)) || a.localeCompare(b));
const suites = process.argv.length > 2 ? process.argv.slice(2) : all;

console.log('Building app …');
const build = spawnSync('dotnet', ['build', PROJECT, '-c', 'Debug', '--artifacts-path', BUILD, '-v', 'q', '-nologo'], { stdio: 'inherit' });
if (build.status !== 0) process.exit(1);
const dll = path.join(BUILD, 'bin', 'BewerbungsPilot', 'debug', 'BewerbungsPilot.dll');

async function waitForApp(app) {
  for (let i = 0; i < 100; i++) {
    if (app.exitCode !== null) throw new Error('App exited during startup.');
    try { await fetch(BASE + '/'); return; } catch { await new Promise((r) => setTimeout(r, 200)); }
  }
  throw new Error('App did not start.');
}

const results = [];
for (const suite of suites) {
  const dataDir = path.join(DIR, '.data', `${suite}-${Date.now()}`);
  fs.mkdirSync(dataDir, { recursive: true });
  const app = spawn('dotnet', [dll, '--contentRoot', PROJECT, '--urls', BASE, '--environment', 'Development',
    '--Storage:DataDirectory', dataDir, '--Geocoding:BaseUrl', 'http://localhost:5399/',
    '--Auth:AllowRegistration', 'true', '--Auth:RequestsPerMinute', '1000', ...(SUITE_SETTINGS[suite] ?? [])],
    { stdio: ['ignore', 'pipe', 'pipe'] });
  let appLog = '';
  app.stdout.on('data', (d) => { appLog += d; });
  app.stderr.on('data', (d) => { appLog += d; });

  console.log(`\n===== ${suite} =====`);
  let status;
  try {
    await waitForApp(app);
    status = spawnSync('node', [path.join(DIR, `${suite}.mjs`)], { stdio: 'inherit', env: { ...process.env, DB_DIR: dataDir } }).status;
  } catch (error) {
    console.log(`FAIL  ${error.message}\n${appLog}`);
    status = 1;
  }
  app.kill();
  await new Promise((r) => app.exitCode !== null ? r() : app.once('exit', r));
  results.push({ suite, ok: status === 0 });
}

console.log('\n===== Summary =====');
for (const { suite, ok } of results) console.log(`${ok ? 'PASS' : 'FAIL'}  ${suite}`);
process.exitCode = results.every((r) => r.ok) ? 0 : 1;
