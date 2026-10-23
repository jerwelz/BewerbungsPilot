// Login for the browser suites (since M9 every page and API call needs an account).
//
// createSession() registers a fresh account through the API and returns
//   fetch      – drop-in replacement for the global fetch: sends the login cookie and,
//                for requests that change data, the antiforgery token (X-CSRF-TOKEN)
//   newContext – like browser.newContext(), but already logged in as the same user
import { randomUUID } from 'node:crypto';

export const BASE = 'http://localhost:5299';
export const PASSWORD = 'ein-sehr-sicheres-passwort';

export async function createSession({ email = `e2e-${randomUUID().slice(0, 8)}@example.org`, password = PASSWORD, register = true } = {}) {
  const jar = new Map();
  let token = null;

  async function rawFetch(url, init = {}) {
    const headers = new Headers(init.headers);
    if (jar.size) headers.set('Cookie', [...jar].map(([name, value]) => `${name}=${value}`).join('; '));
    const response = await globalThis.fetch(url, { ...init, headers });
    for (const line of response.headers.getSetCookie()) {
      const [pair, ...attributes] = line.split(';');
      const name = pair.slice(0, pair.indexOf('=')).trim();
      const value = pair.slice(pair.indexOf('=') + 1).trim();
      const expires = attributes.map((a) => a.trim()).find((a) => a.toLowerCase().startsWith('expires='));
      if (!value || (expires && new Date(expires.slice(8)) < new Date())) jar.delete(name);
      else jar.set(name, value);
    }
    return response;
  }

  async function refreshToken() {
    token = (await (await rawFetch(BASE + '/api/auth/antiforgery')).json()).token;
  }

  async function fetch(url, init = {}) {
    const method = (init.method ?? 'GET').toUpperCase();
    const headers = new Headers(init.headers);
    if (method !== 'GET' && method !== 'HEAD' && !headers.has('X-CSRF-TOKEN')) {
      if (!token) await refreshToken();
      headers.set('X-CSRF-TOKEN', token);
    }
    return rawFetch(url, { ...init, headers });
  }

  await refreshToken();
  const response = await fetch(`${BASE}/api/auth/${register ? 'register' : 'login'}`, {
    method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ email, password }),
  });
  if (!response.ok) throw new Error(`Anmeldung fehlgeschlagen: ${response.status} ${await response.text()}`);
  const user = await response.json();
  await refreshToken(); // the token is bound to the user

  async function newContext(browser, options = {}) {
  // bypassCSP: Playwright's waitForFunction() evaluates its predicate with eval(), which the app's
  // Content-Security-Policy (correctly) forbids. These suites test functionality; the CSP
  // itself is checked without bypass in m11.mjs and in the xUnit tests.
    const context = await browser.newContext({ bypassCSP: true, ...options });
    await context.addCookies([...jar].map(([name, value]) => ({ name, value, url: BASE })));
    return context;
  }

  return { email, password, user, fetch, newContext, cookies: () => new Map(jar) };
}
