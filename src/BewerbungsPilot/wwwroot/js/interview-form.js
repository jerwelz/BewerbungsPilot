// Create/edit dialog for an interview. Built once and reused.
// From the calendar it can also ask which application the interview belongs to.

import { api, ApiError } from './api.js';
import {
  INTERVIEW_TYPES, INTERVIEW_STATUSES, escapeHtml, setButtonSaving, setButtonSaved, resetButton, wait,
  clearFieldErrors, showFieldErrors,
} from './ui.js';

let dialog;
let state = {}; // { applicationId, interviewId, onSaved }

const options = (list) => list.map((item) => `<option value="${item.value}">${escapeHtml(item.label)}</option>`).join('');

function field(name, label, control, { required = false, full = false } = {}) {
  const star = required ? '<span class="required" aria-hidden="true">*</span>' : '';
  return `
    <div class="field${full ? ' field-full' : ''}" data-field="${name}">
      <label for="i-${name}">${label}${star}</label>
      ${control}
      <div class="field-error" id="i-${name}-error"></div>
    </div>`;
}

function buildDialog() {
  dialog = document.createElement('dialog');
  dialog.className = 'dialog';
  dialog.setAttribute('aria-labelledby', 'interview-form-title');
  dialog.innerHTML = `
    <form method="dialog" novalidate>
      <div class="dialog-header">
        <h2 class="dialog-title" id="interview-form-title">Gespräch planen</h2>
        <button type="button" class="icon-btn" data-close aria-label="Schließen">
          <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"
            stroke-linecap="round" aria-hidden="true"><path d="M18 6 6 18M6 6l12 12"/></svg>
        </button>
      </div>
      <div class="dialog-body">
        <div class="form-alert" role="alert" data-form-alert></div>
        <p class="dialog-context" data-context></p>
        <div class="form-grid">
          ${field('applicationId', 'Bewerbung', '<select class="select" id="i-applicationId" name="applicationId" aria-required="true"></select>', { required: true, full: true })}
          ${field('scheduledAt', 'Datum und Uhrzeit', '<input class="input" id="i-scheduledAt" name="scheduledAt" type="datetime-local" aria-required="true">', { required: true })}
          ${field('type', 'Art', `<select class="select" id="i-type" name="type">${options(INTERVIEW_TYPES)}</select>`, { required: true })}
          ${field('location', 'Ort oder Link', '<input class="input" id="i-location" name="location" placeholder="Adresse oder https://…">')}
          ${field('status', 'Status', `<select class="select" id="i-status" name="status">${options(INTERVIEW_STATUSES)}</select>`)}
          ${field('contactPerson', 'Ansprechperson', '<input class="input" id="i-contactPerson" name="contactPerson" placeholder="optional">', { full: true })}
          ${field('notes', 'Notizen', '<textarea class="textarea" id="i-notes" name="notes" rows="4" placeholder="Vorbereitung, Fragen, Ergebnis …"></textarea>', { full: true })}
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

// Reads the form into the JSON the API expects (InterviewInput).
// datetime-local gives "2026-10-28T14:30" – local time, sent without time zone.
function readForm(form) {
  const value = (name) => form.elements[name].value.trim() || null;
  const scheduledAt = value('scheduledAt');
  return {
    scheduledAt: scheduledAt ? `${scheduledAt}:00` : null,
    type: form.elements.type.value,
    status: form.elements.status.value,
    location: value('location'),
    contactPerson: value('contactPerson'),
    notes: value('notes'),
  };
}

async function handleSubmit(event) {
  event.preventDefault();
  const form = event.target;
  const submit = form.querySelector('[data-submit]');
  clearFieldErrors(dialog);

  const applicationId = state.applicationId ?? Number(form.elements.applicationId.value);
  if (!applicationId) {
    showFieldErrors(dialog, { errors: { applicationId: ['Bitte wählen Sie eine Bewerbung aus.'] } }, 'i-');
    return;
  }

  setButtonSaving(submit);
  try {
    const input = readForm(form);
    const saved = state.interviewId
      ? await api.updateInterview(state.interviewId, input)
      : await api.createInterview(applicationId, input);
    setButtonSaved(submit);
    await wait(600);
    dialog.close();
    state.onSaved?.(saved);
  } catch (error) {
    if (!(error instanceof ApiError)) throw error;
    showFieldErrors(dialog, error, 'i-');
  } finally {
    if (submit.disabled) resetButton(submit);
  }
}

// Options:
//   application  – { id, companyName, position } the interview belongs to, or
//   applications – list to choose from (calendar), plus optional defaultDate "YYYY-MM-DD"
//   interview    – existing interview to edit
//   onSaved(interview)
export function openInterviewForm({ application = null, applications = null, interview = null, defaultDate = null, onSaved } = {}) {
  if (!dialog) buildDialog();
  state = { applicationId: application?.id ?? null, interviewId: interview?.id ?? null, onSaved };

  const form = dialog.querySelector('form');
  form.reset();
  clearFieldErrors(dialog);

  // Which application? Fixed (detail panel) or chosen from a list (calendar).
  const chooser = dialog.querySelector('[data-field="applicationId"]');
  chooser.hidden = !applications;
  if (applications) {
    const sorted = [...applications].sort((a, b) => a.companyName.localeCompare(b.companyName, 'de'));
    form.elements.applicationId.innerHTML = '<option value="">Bitte wählen …</option>' + sorted
      .map((a) => `<option value="${a.id}">${escapeHtml(a.companyName)} – ${escapeHtml(a.position)}</option>`).join('');
  }
  dialog.querySelector('[data-context]').textContent = application ? `${application.companyName} – ${application.position}` : '';

  if (interview) {
    form.elements.scheduledAt.value = interview.scheduledAt.slice(0, 16); // "2026-10-28T14:30"
    form.elements.type.value = interview.type;
    form.elements.status.value = interview.status;
    form.elements.location.value = interview.location ?? '';
    form.elements.contactPerson.value = interview.contactPerson ?? '';
    form.elements.notes.value = interview.notes ?? '';
  } else if (defaultDate) {
    form.elements.scheduledAt.value = `${defaultDate}T10:00`;
  }

  const submit = form.querySelector('[data-submit]');
  submit.dataset.label = 'Speichern';
  resetButton(submit);
  dialog.querySelector('#interview-form-title').textContent = interview ? 'Gespräch bearbeiten' : 'Gespräch planen';

  dialog.showModal();
  (applications ? form.elements.applicationId : form.elements.scheduledAt).focus();
}
