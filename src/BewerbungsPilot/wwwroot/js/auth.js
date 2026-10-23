// Page script for login.html and registrieren.html.
import { api, safeReturnUrl } from './api.js';
import { announceSessionChange } from './session.js';
import { clearFieldErrors, showFieldErrors } from './ui.js';

const form = document.querySelector('[data-auth]');
const mode = form.dataset.auth; // "login" | "register"
const notice = document.getElementById('auth-notice');
const params = new URLSearchParams(location.search);
const returnUrl = safeReturnUrl(params.get('returnUrl'));

function goOn() {
  location.replace(returnUrl);
}

async function start() {
  // Already logged in (e.g. via another tab)? Then straight on.
  if (await api.checkSession().catch(() => null)) return goOn();

  if (params.get('expired') === '1')
    notice.textContent = 'Ihre Sitzung ist abgelaufen. Bitte melden Sie sich erneut an.';

  try {
    const { allowRegistration, demoEnabled } = await api.getAuthOptions();
    if (mode === 'login') document.getElementById('register-link').hidden = !allowRegistration;
    if (mode === 'login') document.getElementById('demo-section').hidden = !demoEnabled;
    if (mode === 'register' && !allowRegistration) {
      notice.textContent = 'Die Registrierung ist deaktiviert. Bitte wenden Sie sich an die Person, die BewerbungsPilot betreibt.';
      form.hidden = true;
    }
  } catch {
    // the form still works; the token is fetched again on submit
  }
}

form.addEventListener('submit', async (event) => {
  event.preventDefault();
  clearFieldErrors(form);
  const submit = form.querySelector('[type="submit"]');
  const input = { email: form.elements.email.value.trim(), password: form.elements.password.value };
  const missing = {};
  if (!input.email) missing.email = ['Bitte geben Sie Ihre E-Mail-Adresse ein.'];
  if (!input.password) missing.password = ['Bitte geben Sie Ihr Passwort ein.'];
  if (Object.keys(missing).length) return showFieldErrors(form, { errors: missing }, 'a-');

  submit.disabled = true;
  try {
    if (mode === 'login') await api.login({ ...input, rememberMe: form.elements.rememberMe.checked });
    else await api.register(input);
    announceSessionChange(); // other open tabs reload with the new account
    goOn();
  } catch (error) {
    submit.disabled = false;
    form.elements.password.value = '';
    showFieldErrors(form, error, 'a-');
  }
});

// Demo mode: a fresh demo account with fictional data, then on to the dashboard.
document.getElementById('demo-start')?.addEventListener('click', async (event) => {
  const button = event.currentTarget;
  const alert = document.getElementById('demo-alert');
  alert.textContent = '';
  button.disabled = true;
  try {
    await api.startDemo();
    announceSessionChange();
    location.replace('/');
  } catch (error) {
    button.disabled = false;
    alert.textContent = error.data?.title ?? 'Die Demo konnte nicht gestartet werden.';
  }
});

start();
