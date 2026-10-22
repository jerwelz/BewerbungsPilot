// Create dialog for an application. Built once and reused.

import { api, ApiError } from './api.js';
import { STATUSES, escapeHtml, setButtonSaving, setButtonSaved, resetButton, wait } from './ui.js';

let dialog;
let onSavedCallback;

function buildDialog() {
  dialog = document.createElement('dialog');
  dialog.className = 'dialog';
  dialog.setAttribute('aria-labelledby', 'application-form-title');

  const statusOptions = STATUSES
    .map((s) => `<option value="${s.value}">${escapeHtml(s.label)}</option>`)
    .join('');

  // novalidate: we show the server's German validation messages instead of the browser's.
  dialog.innerHTML = `
    <form method="dialog" novalidate>
      <div class="dialog-header">
        <h2 class="dialog-title" id="application-form-title">Neue Bewerbung</h2>
        <button type="button" class="icon-btn" data-close aria-label="Schließen">
          <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"
            stroke-linecap="round" aria-hidden="true"><path d="M18 6 6 18M6 6l12 12"/></svg>
        </button>
      </div>
      <div class="dialog-body">
        <div class="form-alert" role="alert" data-form-alert></div>
        <div class="form-grid">
          ${field('companyName', 'Unternehmen', '<input class="input" id="f-companyName" name="companyName" autocomplete="organization" aria-required="true">', true)}
          ${field('position', 'Position', '<input class="input" id="f-position" name="position" aria-required="true">', true)}
          ${field('location', 'Ort', '<input class="input" id="f-location" name="location" placeholder="z. B. Hamburg oder Remote">')}
          ${field('status', 'Status', `<select class="select" id="f-status" name="status">${statusOptions}</select>`)}
          ${field('deadline', 'Bewerbungsfrist', '<input class="input" id="f-deadline" name="deadline" type="date">')}
          ${field('appliedOn', 'Beworben am', '<input class="input" id="f-appliedOn" name="appliedOn" type="date">')}
          ${field('jobUrl', 'Link zur Stellenanzeige', '<input class="input" id="f-jobUrl" name="jobUrl" type="url" inputmode="url" placeholder="https://…">', false, true)}
          ${field('notes', 'Notizen', '<textarea class="textarea" id="f-notes" name="notes" rows="4"></textarea>', false, true)}
        </div>
      </div>
      <div class="dialog-footer">
        <button type="button" class="btn btn-secondary" data-close>Abbrechen</button>
        <button type="submit" class="btn btn-primary" data-submit>Speichern</button>
      </div>
    </form>`;

  document.body.append(dialog);

  dialog.querySelectorAll('[data-close]').forEach((b) => b.addEventListener('click', () => dialog.close()));
  dialog.querySelector('form').addEventListener('submit', handleSubmit);
}

function field(name, label, control, required = false, full = false) {
  const star = required ? '<span class="required" aria-hidden="true">*</span>' : '';
  return `
    <div class="field${full ? ' field-full' : ''}" data-field="${name}">
      <label for="f-${name}">${label}${star}</label>
      ${control}
      <div class="field-error" id="f-${name}-error"></div>
    </div>`;
}

// Reads the form into the JSON shape the API expects (ApplicationInput).
// Empty fields become null.
function readForm(form) {
  const value = (name) => form.elements[name].value.trim() || null;
  return {
    companyName: value('companyName'),
    position: value('position'),
    location: value('location'),
    jobUrl: value('jobUrl'),
    deadline: value('deadline'),
    appliedOn: value('appliedOn'),
    status: form.elements.status.value,
    notes: value('notes'),
  };
}

function clearErrors() {
  dialog.querySelector('[data-form-alert]').textContent = '';
  dialog.querySelectorAll('.field').forEach((f) => {
    f.classList.remove('has-error');
    f.querySelector('.field-error').textContent = '';
    f.querySelector('.input, .select, .textarea').removeAttribute('aria-invalid');
  });
}

function showErrors(error) {
  let firstInvalid = null;
  for (const [name, messages] of Object.entries(error.errors)) {
    const fieldEl = dialog.querySelector(`[data-field="${name}"]`);
    if (!fieldEl) continue;
    const control = fieldEl.querySelector('.input, .select, .textarea');
    fieldEl.classList.add('has-error');
    fieldEl.querySelector('.field-error').textContent = messages.join(' ');
    control.setAttribute('aria-invalid', 'true');
    control.setAttribute('aria-describedby', `f-${name}-error`);
    firstInvalid ??= control;
  }
  if (firstInvalid) firstInvalid.focus();
  else dialog.querySelector('[data-form-alert]').textContent = error.message;
}

async function handleSubmit(event) {
  event.preventDefault();
  const form = event.target;
  const submit = form.querySelector('[data-submit]');
  clearErrors();
  setButtonSaving(submit);

  try {
    const saved = await api.createApplication(readForm(form));
    setButtonSaved(submit);
    await wait(650);
    dialog.close();
    onSavedCallback?.(saved);
  } catch (error) {
    if (error instanceof ApiError) showErrors(error);
    else throw error;
  } finally {
    resetButton(submit);
  }
}

export function openApplicationForm({ onSaved } = {}) {
  if (!dialog) buildDialog();
  onSavedCallback = onSaved;

  const form = dialog.querySelector('form');
  form.reset();
  clearErrors();
  dialog.showModal();
  form.elements.companyName.focus();
}
