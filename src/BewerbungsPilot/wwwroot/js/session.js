// Login state of the current page: who is logged in, logout, and protection against showing
// private data after logout or after another user has logged in (other tab, "Back" button).
// Loaded by every page through ui.js (renderSidebar) and by the login/registration pages.

import { api, setPageUser } from './api.js';

const SESSION_KEY = 'bewerbungspilot.session';         // changes on every login/logout
const LEGACY_ORIGIN_KEY = 'bewerbungspilot.origin';    // before M9: one starting point for everybody

// The map starting point is stored per user, so the next user of this browser never sees it.
export const originKey = (userId) => `bewerbungspilot.origin.${userId}`;

let userPromise = null;

// Resolves with { id, email }. Redirects to the login page if nobody is logged in.
export function requireUser() {
  userPromise ??= api.me().then((user) => {
    setPageUser(user.id);
    removeStorage(LEGACY_ORIGIN_KEY);
    return user;
  });
  return userPromise;
}

// Tells other tabs that the logged-in user has changed (see the "storage" listener below).
export function announceSessionChange() {
  try {
    localStorage.setItem(SESSION_KEY, `${Date.now()}`);
  } catch {
    // storage blocked: the other tabs notice at their next request (401 / CSRF check)
  }
}

export async function logout() {
  const user = await userPromise?.catch(() => null);
  try {
    await api.logout();
  } catch {
    // the session is ended in the browser either way
  }
  if (user) removeStorage(originKey(user.id));
  announceSessionChange();
  location.replace('/willkommen.html'); // forwards to the login page when demo mode is off
}

function removeStorage(key) {
  try {
    localStorage.removeItem(key);
  } catch {
    // nothing stored
  }
}

function hidePrivateContent() {
  document.querySelector('.main')?.setAttribute('hidden', '');
  document.querySelectorAll('dialog[open]').forEach((dialog) => dialog.close());
}

// Another tab logged in or out: this page may show the previous user's data → reload now.
window.addEventListener('storage', (event) => {
  if (event.key !== SESSION_KEY) return;
  hidePrivateContent();
  location.reload();
});

// Page restored from the back/forward cache (e.g. "Back" after logout): check the session first.
window.addEventListener('pageshow', async (event) => {
  if (!event.persisted) return;
  hidePrivateContent();
  const pageUser = await userPromise?.catch(() => null);
  const current = await api.checkSession().catch(() => null);
  if (pageUser && current?.id === pageUser.id) document.querySelector('.main')?.removeAttribute('hidden');
  else location.reload();
});
