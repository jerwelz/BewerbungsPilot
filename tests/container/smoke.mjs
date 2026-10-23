// Smoke test for the container image (run by the "container" job in .github/workflows/ci.yml).
// The container runs like on Fly.io: Production, demo mode, data on a volume, behind a proxy that
// terminates HTTPS – simulated here with the X-Forwarded-Proto header.
//
//   node smoke.mjs <state-file>                  → first start: demo, pages, limits
//   node smoke.mjs <state-file> after-restart    → after "docker restart": same login, same data
import fs from 'node:fs';

const BASE = process.env.BASE ?? 'http://localhost:8080';
const [stateFile, phase = 'first'] = process.argv.slice(2);
const check = (cond, msg) => { console.log(`${cond ? 'PASS' : 'FAIL'}  ${msg}`); if (!cond) process.exitCode = 1; };

const jar = new Map(phase === 'after-restart' ? Object.entries(JSON.parse(fs.readFileSync(stateFile, 'utf8'))) : []);
let token = null;

// fetch through the "proxy": HTTPS was used by the visitor; cookies handled by hand
// (they are Secure cookies, which a plain-HTTP client would otherwise refuse to send).
async function call(method, path, { body, headers = {}, proxied = true } = {}) {
  const response = await fetch(BASE + path, {
    method,
    redirect: 'manual',
    headers: {
      ...(proxied ? { 'X-Forwarded-Proto': 'https', 'Fly-Client-IP': '203.0.113.10' } : {}),
      ...(jar.size ? { Cookie: [...jar].map(([k, v]) => `${k}=${v}`).join('; ') } : {}),
      ...(token && method !== 'GET' ? { 'X-CSRF-TOKEN': token } : {}),
      ...(typeof body === 'string' || body === undefined ? {} : { 'Content-Type': 'application/json' }),
      ...headers,
    },
    body: body === undefined ? undefined : typeof body === 'string' ? body : JSON.stringify(body),
  });
  for (const cookie of response.headers.getSetCookie()) {
    const [pair] = cookie.split(';');
    const [name, ...rest] = pair.split('=');
    const value = rest.join('=');
    if (value) jar.set(name, value); else jar.delete(name);
  }
  return response;
}

async function refreshToken() {
  token = (await (await call('GET', '/api/auth/antiforgery')).json()).token;
}

if (phase === 'first') {
  const health = await call('GET', '/health', { proxied: false });
  check(health.status === 200 && (await health.text()) === 'Healthy', 'health check over plain HTTP → 200 Healthy');

  const plain = await call('GET', '/willkommen.html', { proxied: false });
  check(plain.status === 307 && plain.headers.get('location') === 'https://localhost/willkommen.html',
    `direct HTTP request is redirected to HTTPS (${plain.status} ${plain.headers.get('location')})`);

  const landing = await call('GET', '/willkommen.html');
  check(landing.status === 200 && (await landing.text()).includes('Demo starten'), 'start page via proxy → 200');
  check(landing.headers.get('content-security-policy')?.includes("default-src 'self'"), 'Content-Security-Policy header');
  // HSTS is not checked here: ASP.NET Core never sends it for "localhost" (xUnit checks it with a real host name).

  await refreshToken();
  check([...jar.keys()].includes('__Host-BewerbungsPilot.Antiforgery'), 'antiforgery cookie with __Host- prefix');
  const start = await call('POST', '/api/demo/start');
  check(start.status === 200 && jar.has('__Host-BewerbungsPilot'), `demo start → ${start.status}, login cookie set`);
  await refreshToken();

  const applications = await (await call('GET', '/api/applications')).json();
  check(applications.length === 12, `demo contains 12 applications (${applications.length})`);
  let pdf = null;
  for (const a of applications) {
    const docs = await (await call('GET', `/api/applications/${a.id}/documents`)).json();
    if (docs.length) { pdf = docs[0]; break; }
  }
  const preview = pdf && await call('GET', `/api/documents/${pdf.id}/preview`);
  check(preview?.status === 200 && preview.headers.get('content-type') === 'application/pdf', 'demo PDF readable from the volume');

  const register = await call('POST', '/api/auth/register', { body: { email: 'echt@example.org', password: 'ein-sehr-sicheres-passwort' } });
  check(register.status === 403, `registration closed (${register.status})`);

  const huge = await call('POST', '/api/applications', { body: JSON.stringify({ companyName: 'x', position: 'y', notes: 'z'.repeat(2_000_000) }), headers: { 'Content-Type': 'application/json' } });
  check(huge.status === 413, `request body over 1 MB refused (${huge.status})`);

  fs.writeFileSync(stateFile, JSON.stringify(Object.fromEntries(jar)));
} else {
  const me = await call('GET', '/api/auth/me');
  check(me.status === 200 && (await me.json()).isDemo === true, 'after restart: same demo login still valid (keys persisted)');
  const applications = await (await call('GET', '/api/applications')).json();
  check(applications.length === 12, `after restart: demo data still there (${applications.length})`);
}
