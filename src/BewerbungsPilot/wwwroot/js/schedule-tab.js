// "Termine" tab of the detail panel: the application deadline and its interviews.

import { api } from './api.js';
import {
  escapeHtml, formatDate, formatLocalDateTime, statePill, INTERVIEW_TYPES, labelOf, showToast, confirmDialog,
} from './ui.js';
import { openInterviewForm } from './interview-form.js';

const monthShort = new Intl.DateTimeFormat('de-DE', { month: 'short' });
const weekdayShort = new Intl.DateTimeFormat('de-DE', { weekday: 'short' });

const ICON = {
  edit: '<path d="M12 20h9M16.5 3.5a2.1 2.1 0 0 1 3 3L7 19l-4 1 1-4Z"/>',
  trash: '<path d="M3 6h18M8 6V4h8v2M19 6l-1 14H6L5 6"/>',
  check: '<path d="M20 6 9 17l-5-5"/>',
  cancel: '<circle cx="12" cy="12" r="9"/><path d="m5.7 5.7 12.6 12.6"/>',
};
const icon = (name) => `<svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor"
  stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">${ICON[name]}</svg>`;

// "2026-10-28" or "2026-10-28T14:30:00" → small calendar sheet "28 / Okt."
export function dateBlock(isoText, extraClass = '') {
  const date = new Date(isoText.length === 10 ? `${isoText}T00:00:00` : isoText); // date-only text: read as local midnight
  return `<span class="date-block ${extraClass}" aria-hidden="true">
    <span class="date-month">${monthShort.format(date)}</span><span class="date-day">${date.getDate()}</span></span>`;
}

function daysUntil(isoDate) {
  const [y, m, d] = isoDate.split('-').map(Number);
  const target = new Date(y, m - 1, d);
  const now = new Date();
  const today = new Date(now.getFullYear(), now.getMonth(), now.getDate());
  return Math.round((target - today) / 86_400_000); // Math.round absorbs the 1-hour DST difference
}

function deadlineHint(application) {
  switch (application.deadlineState) {
    case 'Overdue': return 'Die Frist ist abgelaufen, die Bewerbung wurde noch nicht versendet.';
    case 'Done': return 'Erledigt – die Bewerbung wurde versendet.';
    case 'DueSoon': {
      const days = daysUntil(application.deadline);
      return days === 0 ? 'Die Frist endet heute.' : days === 1 ? 'Die Frist endet morgen.' : `Noch ${days} Tage.`;
    }
    default: return `Noch ${daysUntil(application.deadline)} Tage.`;
  }
}

export function scheduleHtml(application, interviews) {
  const deadline = application.deadline
    ? `<div class="deadline-box deadline-${application.deadlineState}">
         ${dateBlock(application.deadline)}
         <div class="deadline-main">
           <div class="deadline-title">${formatDate(application.deadline)} ${statePill('Deadline', application.deadlineState)}</div>
           <div class="muted">${deadlineHint(application)}</div>
         </div>
       </div>`
    : '<p class="muted schedule-empty">Keine Bewerbungsfrist hinterlegt.</p>';

  const list = interviews.length === 0
    ? '<p class="muted schedule-empty">Noch keine Gespräche eingetragen.</p>'
    : `<ul class="interview-list">${interviews.map(interviewHtml).join('')}</ul>`;

  return `
    <section class="schedule-section" aria-labelledby="deadline-heading">
      <div class="schedule-header">
        <h3 class="section-title" id="deadline-heading">Bewerbungsfrist</h3>
        <button type="button" class="btn btn-ghost btn-sm" data-edit-deadline>${application.deadline ? 'Frist ändern' : 'Frist festlegen'}</button>
      </div>
      ${deadline}
    </section>

    <section class="schedule-section" aria-labelledby="interviews-heading">
      <div class="schedule-header">
        <h3 class="section-title" id="interviews-heading">Vorstellungsgespräche</h3>
        <button type="button" class="btn btn-primary btn-sm" data-new-interview>Gespräch planen</button>
      </div>
      ${list}
    </section>`;
}

function interviewHtml(i) {
  const date = new Date(i.scheduledAt);
  const isUrl = /^https?:\/\//i.test(i.location ?? '');
  const location = !i.location ? ''
    : isUrl
      ? `<a href="${escapeHtml(i.location)}" target="_blank" rel="noopener noreferrer" class="external-link">Meeting-Link öffnen<span class="visually-hidden"> (öffnet in neuem Tab)</span></a>`
      : `<span>${escapeHtml(i.location)}</span>`;
  const label = `${labelOf(INTERVIEW_TYPES, i.type)}-Gespräch am ${formatLocalDateTime(i.scheduledAt)}`;

  return `
    <li class="interview-row interview-${i.state}" data-interview-id="${i.id}">
      ${dateBlock(i.scheduledAt)}
      <div class="interview-main">
        <div class="interview-title">
          ${escapeHtml(labelOf(INTERVIEW_TYPES, i.type))} · ${weekdayShort.format(date)}, ${formatLocalDateTime(i.scheduledAt)}
          ${statePill('Interview', i.state)}
        </div>
        <div class="interview-meta">
          ${location}
          ${i.contactPerson ? `<span>Ansprechperson: ${escapeHtml(i.contactPerson)}</span>` : ''}
        </div>
        ${i.notes ? `<p class="interview-notes">${escapeHtml(i.notes)}</p>` : ''}
      </div>
      <div class="interview-actions">
        ${i.state === 'Overdue' ? `<button type="button" class="icon-btn" data-complete aria-label="Als abgeschlossen markieren: ${label}" title="Als abgeschlossen markieren">${icon('check')}</button>` : ''}
        ${i.status === 'Geplant' ? `<button type="button" class="icon-btn" data-cancel aria-label="Absagen: ${label}" title="Absagen">${icon('cancel')}</button>` : ''}
        <button type="button" class="icon-btn" data-edit-interview aria-label="Bearbeiten: ${label}" title="Bearbeiten">${icon('edit')}</button>
        <button type="button" class="icon-btn icon-btn-danger" data-delete-interview aria-label="Löschen: ${label}" title="Löschen">${icon('trash')}</button>
      </div>
    </li>`;
}

// onChanged(): reload the panel; onEditApplication(): open the application form (deadline).
export function setUpScheduleTab(container, application, interviews, { onChanged, onEditApplication }) {
  container.querySelector('[data-edit-deadline]').addEventListener('click', onEditApplication);
  container.querySelector('[data-new-interview]').addEventListener('click', () =>
    openInterviewForm({
      application,
      onSaved: () => {
        showToast('Gespräch gespeichert');
        onChanged();
      },
    }));

  container.addEventListener('click', async (event) => {
    const row = event.target.closest('[data-interview-id]');
    if (!row) return;
    const interview = interviews.find((i) => i.id === Number(row.dataset.interviewId));

    if (event.target.closest('[data-edit-interview]')) {
      openInterviewForm({
        application,
        interview,
        onSaved: () => {
          showToast('Gespräch gespeichert');
          onChanged();
        },
      });
    } else if (event.target.closest('[data-cancel]')) {
      await setStatus(interview, 'Abgesagt', 'Gespräch abgesagt');
      onChanged();
    } else if (event.target.closest('[data-complete]')) {
      await setStatus(interview, 'Abgeschlossen', 'Gespräch als abgeschlossen markiert');
      onChanged();
    } else if (event.target.closest('[data-delete-interview]')) {
      const confirmed = await confirmDialog({
        title: 'Gespräch löschen?',
        message: `Das Gespräch am ${formatLocalDateTime(interview.scheduledAt)} wird dauerhaft gelöscht. Wenn es nur nicht stattfindet, wählen Sie besser „Absagen“.`,
        confirmLabel: 'Endgültig löschen',
        danger: true,
      });
      if (!confirmed) return;
      try {
        await api.deleteInterview(interview.id);
        showToast('Gespräch gelöscht');
      } catch (error) {
        showToast(error.message, 'error');
      }
      onChanged();
    }
  });
}

// Changes only the status; all other values are sent unchanged.
async function setStatus(interview, status, message) {
  try {
    await api.updateInterview(interview.id, {
      scheduledAt: interview.scheduledAt,
      type: interview.type,
      location: interview.location,
      contactPerson: interview.contactPerson,
      notes: interview.notes,
      status,
    });
    showToast(message);
  } catch (error) {
    showToast(error.message, 'error');
  }
}
