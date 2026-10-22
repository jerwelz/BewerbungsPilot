// Create/edit dialog for an application. Built once and reused.

import { api, ApiError } from './api.js';
import { STATUSES, escapeHtml, statusLabel, formatDateTime, setButtonSaving, setButtonSaved, resetButton, wait } from './ui.js';

let dialog;
let onSavedCallback;
let editingId = null;          // null = create, number = edit
let confirmedDuplicateKey = null; // company|position the user already chose to save anyway

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
        <div class="form-warning" role="alert" data-duplicate-warning hidden></div>
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
  const form = dialog.querySelector('form');
  form.addEventListener('submit', handleSubmit);
  // Changing company or position invalidates an earlier "save anyway" decision.
  form.elements.companyName.addEventListener('input', hideDuplicateWarning);
  form.elements.position.addEventListener('input', hideDuplicateWarning);
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

function fillForm(form, application) {
  for (const name of ['companyName', 'position', 'location', 'jobUrl', 'deadline', 'appliedOn', 'notes']) {
    form.elements[name].value = application?.[name] ?? '';
  }
  form.elements.status.value = application?.status ?? 'Entwurf';
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

// ---------- Duplicate warning ----------

function duplicateKey(input) {
  return `${input.companyName ?? ''}|${input.position ?? ''}`.toLowerCase();
}

function showDuplicateWarning(duplicates) {
  const box = dialog.querySelector('[data-duplicate-warning]');
  const items = duplicates.map((d) =>
    `<li>${escapeHtml(d.companyName)} – ${escapeHtml(d.position)} <span class="muted">(${escapeHtml(statusLabel(d.status))}, angelegt am ${formatDateTime(d.createdAt)})</span></li>`).join('');
  box.innerHTML = `
    <strong>Mögliche doppelte Bewerbung</strong>
    <p>Für dieses Unternehmen und diese Position gibt es bereits ${duplicates.length === 1 ? 'eine Bewerbung' : `${duplicates.length} Bewerbungen`}:</p>
    <ul>${items}</ul>
    <p>Prüfen Sie die Angaben. Sie können trotzdem speichern.</p>`;
  box.hidden = false;
  dialog.querySelector('[data-submit]').textContent = 'Trotzdem speichern';
}

function hideDuplicateWarning() {
  const box = dialog.querySelector('[data-duplicate-warning]');
  if (box.hidden) return;
  box.hidden = true;
  confirmedDuplicateKey = null;
  dialog.querySelector('[data-submit]').textContent = 'Speichern';
}

// Returns true if saving should stop to show the duplicate warning first.
async function mustWarnAboutDuplicates(input) {
  if (!input.companyName || !input.position) return false; // server validation will complain
  if (confirmedDuplicateKey === duplicateKey(input)) return false; // user already saw the warning

  const duplicates = await api.findDuplicates(input.companyName, input.position, editingId);
  if (duplicates.length === 0) return false;

  showDuplicateWarning(duplicates);
  confirmedDuplicateKey = duplicateKey(input);
  return true;
}

// ---------- Saving ----------

async function handleSubmit(event) {
  event.preventDefault();
  const form = event.target;
  const submit = form.querySelector('[data-submit]');
  const input = readForm(form);
  clearErrors();

  try {
    if (await mustWarnAboutDuplicates(input)) return;

    setButtonSaving(submit);
    const saved = editingId === null
      ? await api.createApplication(input)
      : (await api.updateApplication(editingId, input)).application;
    setButtonSaved(submit);
    await wait(650);
    dialog.close();
    onSavedCallback?.(saved);
  } catch (error) {
    if (error instanceof ApiError) showErrors(error);
    else throw error;
  } finally {
    if (submit.disabled) resetButton(submit);
  }
}

// Opens the dialog. Pass `application` to edit it, leave it out to create a new one.
// `onSaved` receives the saved application.
export function openApplicationForm({ application = null, onSaved } = {}) {
  if (!dialog) buildDialog();
  onSavedCallback = onSaved;
  editingId = application?.id ?? null;
  confirmedDuplicateKey = null;

  const form = dialog.querySelector('form');
  form.reset();
  fillForm(form, application);
  clearErrors();
  hideDuplicateWarning();
  const submit = form.querySelector('[data-submit]');
  submit.dataset.label = 'Speichern';
  resetButton(submit);
  dialog.querySelector('#application-form-title').textContent = application ? 'Bewerbung bearbeiten' : 'Neue Bewerbung';

  dialog.showModal();
  form.elements.companyName.focus();
}
