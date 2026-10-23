// Page script for willkommen.html – the start page for visitors who are not logged in.
// Without demo mode (private use) the page is skipped and the login page opens directly.
import { api, safeReturnUrl } from './api.js';
import { bindDemoButton } from './demo-start.js';

const params = new URLSearchParams(location.search);

async function start() {
  // Already logged in (e.g. via another tab)? Then straight on.
  if (await api.checkSession().catch(() => null)) return location.replace(safeReturnUrl(params.get('returnUrl')));

  const options = await api.getAuthOptions().catch(() => null);
  if (!options?.demoEnabled) return location.replace(`/login.html${location.search}`);

  // Login links keep returnUrl/expired, so a real account gets back to the page it came from.
  document.querySelectorAll('[data-login-link]').forEach((link) => { link.href = `/login.html${location.search}`; });
  if (params.get('expired') === '1')
    document.getElementById('landing-notice').textContent =
      'Ihre Sitzung ist abgelaufen. Demo-Konten werden nach 4 Stunden gelöscht – starten Sie einfach eine neue Demo.';

  bindDemoButton(document.getElementById('demo-start'), document.getElementById('demo-alert'));
  document.body.hidden = false;
}

start();
