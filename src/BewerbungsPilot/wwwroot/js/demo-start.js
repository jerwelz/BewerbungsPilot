// "Demo starten" button (landing page and login page, demo mode only): a fresh demo account
// with fictional data, then on to the dashboard.
import { api } from './api.js';
import { announceSessionChange } from './session.js';

export function bindDemoButton(button, alert) {
  button.addEventListener('click', async () => {
    alert.textContent = '';
    button.disabled = true;
    button.textContent = 'Demo wird vorbereitet …';
    try {
      await api.startDemo();
      announceSessionChange(); // other open tabs reload with the new account
      location.replace('/');
    } catch (error) {
      button.disabled = false;
      button.textContent = 'Demo starten';
      alert.textContent = error.message || 'Die Demo konnte nicht gestartet werden.';
    }
  });
}
