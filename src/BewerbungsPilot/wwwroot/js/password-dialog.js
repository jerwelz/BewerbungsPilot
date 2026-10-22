// "Passwort ändern" dialog (opened from the sidebar).
import { api } from './api.js';
import { clearFieldErrors, showFieldErrors, showToast, setButtonSaving, resetButton } from './ui.js';

export function openPasswordDialog() {
  const dialog = document.createElement('dialog');
  dialog.className = 'dialog dialog-sm';
  dialog.setAttribute('aria-labelledby', 'password-title');
  dialog.innerHTML = `
    <form novalidate>
      <div class="dialog-body">
        <h2 class="dialog-title" id="password-title">Passwort ändern</h2>
        <p class="dialog-text">Danach werden Sie auf allen anderen Geräten abgemeldet.</p>
        <div class="form-alert" data-form-alert role="alert"></div>
        <div class="field" data-field="currentPassword">
          <label for="p-currentPassword">Aktuelles Passwort</label>
          <input class="input" id="p-currentPassword" name="currentPassword" type="password" autocomplete="current-password" required>
          <div class="field-error" id="p-currentPassword-error"></div>
        </div>
        <div class="field" data-field="newPassword">
          <label for="p-newPassword">Neues Passwort</label>
          <input class="input" id="p-newPassword" name="newPassword" type="password" autocomplete="new-password" minlength="12" required
            aria-describedby="p-newPassword-hint">
          <p class="field-hint" id="p-newPassword-hint">Mindestens 12 Zeichen. Ein langer Satz ist leichter zu merken als Sonderzeichen.</p>
          <div class="field-error" id="p-newPassword-error"></div>
        </div>
      </div>
      <div class="dialog-footer">
        <button class="btn btn-secondary" type="button" data-cancel>Abbrechen</button>
        <button class="btn btn-primary" type="submit" data-submit>Passwort ändern</button>
      </div>
    </form>`;
  document.body.append(dialog);

  const form = dialog.querySelector('form');
  const submit = dialog.querySelector('[data-submit]');
  dialog.querySelector('[data-cancel]').addEventListener('click', () => dialog.close());
  dialog.addEventListener('close', () => setTimeout(() => dialog.remove(), 300));

  form.addEventListener('submit', async (event) => {
    event.preventDefault();
    clearFieldErrors(dialog);
    setButtonSaving(submit);
    try {
      await api.changePassword({ currentPassword: form.elements.currentPassword.value, newPassword: form.elements.newPassword.value });
      dialog.close();
      showToast('Ihr Passwort wurde geändert.');
    } catch (error) {
      resetButton(submit);
      showFieldErrors(dialog, error, 'p-');
    }
  });

  dialog.showModal();
}
