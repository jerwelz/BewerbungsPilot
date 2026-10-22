// Page script for kalender.html: month grid (desktop), agenda list (narrow screens)
// and a day panel. All events come from GET /api/calendar for the visible weeks.

import { api } from './api.js';
import {
  renderSidebar, escapeHtml, toIsoDate, todayIso, formatTime, statePill, labelOf, INTERVIEW_TYPES, showToast,
} from './ui.js';
import { openDetailPanel } from './detail-panel.js';
import { openInterviewForm } from './interview-form.js';

renderSidebar('calendar');

const MAX_CHIPS = 3; // events shown per day cell; the rest as "+2 weitere"
const monthLabel = new Intl.DateTimeFormat('de-DE', { month: 'long', year: 'numeric' });
const longDay = new Intl.DateTimeFormat('de-DE', { weekday: 'long', day: 'numeric', month: 'long', year: 'numeric' });
const agendaDay = new Intl.DateTimeFormat('de-DE', { weekday: 'short', day: 'numeric', month: 'long' });

const grid = document.getElementById('calendar-grid');
const dayPanel = document.getElementById('day-panel');
const agenda = document.getElementById('agenda');
const errorBox = document.getElementById('calendar-error');

let month = firstOfMonth(new Date()); // first day of the shown month (local time)
let selected = todayIso();            // selected day as "YYYY-MM-DD"
let events = [];                      // events of the visible weeks, sorted by the server

function firstOfMonth(date) {
  return new Date(date.getFullYear(), date.getMonth(), 1);
}

// "2026-10-28" → local Date at midnight (never new Date("2026-10-28"), which means UTC midnight)
function parseIsoDate(iso) {
  const [y, m, d] = iso.split('-').map(Number);
  return new Date(y, m - 1, d);
}

// Visible weeks: from the Monday on/before the 1st to the Sunday on/after the last day.
// setDate() counts calendar days, so daylight saving changes cannot shift a day.
function visibleDays() {
  const start = new Date(month);
  start.setDate(1 - ((start.getDay() + 6) % 7)); // getDay(): 0 = Sunday → Monday-based
  const last = new Date(month.getFullYear(), month.getMonth() + 1, 0);
  const end = new Date(last);
  end.setDate(last.getDate() + (6 - ((last.getDay() + 6) % 7)));

  const days = [];
  for (const day = new Date(start); day <= end; day.setDate(day.getDate() + 1)) days.push(new Date(day));
  return days;
}

const eventsOn = (iso) => events.filter((e) => e.date === iso);
const inShownMonth = (iso) => parseIsoDate(iso).getMonth() === month.getMonth()
  && parseIsoDate(iso).getFullYear() === month.getFullYear();

// ---------- Loading ----------

async function load() {
  const days = visibleDays();
  try {
    events = await api.getCalendar(toIsoDate(days[0]), toIsoDate(days.at(-1)));
    errorBox.textContent = '';
  } catch (error) {
    events = [];
    errorBox.textContent = `Termine konnten nicht geladen werden: ${error.message}`;
  }
  // Label and grid are updated together, so they never show different months.
  document.getElementById('month-label').textContent = monthLabel.format(month);
  renderGrid(days);
  renderDayPanel();
  renderAgenda();
}

// ---------- Month grid ----------

function renderGrid(days) {
  const today = todayIso();
  grid.innerHTML = days.map((day) => {
    const iso = toIsoDate(day);
    const dayEvents = eventsOn(iso);
    const classes = ['cal-day'];
    if (!inShownMonth(iso)) classes.push('is-outside');
    if (iso === today) classes.push('is-today');
    if (iso === selected) classes.push('is-selected');
    if (day.getDay() === 0 || day.getDay() === 6) classes.push('is-weekend');

    const count = dayEvents.length;
    const label = `${longDay.format(day)}${count ? `, ${count} ${count === 1 ? 'Termin' : 'Termine'}` : ''}${iso === today ? ' (heute)' : ''}`;
    const chips = dayEvents.slice(0, MAX_CHIPS).map(chipHtml).join('');
    const more = count > MAX_CHIPS ? `<li class="cal-more">+${count - MAX_CHIPS} weitere</li>` : '';

    return `
      <div class="${classes.join(' ')}" data-date="${iso}">
        <button type="button" class="cal-date" aria-label="${label}" aria-pressed="${iso === selected}">${day.getDate()}</button>
        <ul class="cal-events">${chips}${more}</ul>
      </div>`;
  }).join('');
}

function chipHtml(e) {
  const text = e.kind === 'Interview'
    ? `<span class="chip-time">${e.time.slice(0, 5)}</span> ${escapeHtml(e.companyName)}`
    : `Frist: ${escapeHtml(e.companyName)}`;
  const title = `${eventTitle(e)} – ${e.companyName}`;
  return `<li><button type="button" class="cal-chip kind-${e.kind} state-${e.state}" data-application="${e.applicationId}"
    title="${escapeHtml(title)}" aria-label="${escapeHtml(title)}, Details öffnen">${text}</button></li>`;
}

grid.addEventListener('click', (event) => {
  const chip = event.target.closest('[data-application]');
  if (chip) {
    openApplication(Number(chip.dataset.application));
    return;
  }
  const cell = event.target.closest('[data-date]');
  if (cell) selectDay(cell.dataset.date);
});

// Selecting a day of the shown month needs no new data; a day of the
// neighbouring month (grey cells) switches the month.
async function selectDay(iso) {
  if (!inShownMonth(iso)) {
    await showDay(iso);
    return;
  }
  selected = iso;
  renderGrid(visibleDays());
  renderDayPanel();
  grid.querySelector(`[data-date="${iso}"] .cal-date`)?.focus();
}

// ---------- Day panel & agenda (same item markup) ----------

function eventTitle(e) {
  return e.kind === 'Interview'
    ? `Vorstellungsgespräch · ${labelOf(INTERVIEW_TYPES, e.interviewType)}`
    : 'Bewerbungsfrist';
}

function eventItemHtml(e) {
  return `
    <li>
      <button type="button" class="agenda-item kind-${e.kind}" data-application="${e.applicationId}">
        <span class="agenda-time">${e.kind === 'Interview' ? formatTime(e.time) : 'ganztägig'}</span>
        <span class="agenda-main">
          <span class="agenda-title">${eventTitle(e)}</span>
          <span class="agenda-meta">${escapeHtml(e.companyName)} · ${escapeHtml(e.position)}</span>
          ${e.location ? `<span class="agenda-meta">${escapeHtml(e.location)}</span>` : ''}
        </span>
        ${statePill(e.kind, e.state)}
      </button>
    </li>`;
}

function renderDayPanel() {
  const dayEvents = eventsOn(selected);
  dayPanel.innerHTML = `
    <h2 class="card-title">${longDay.format(parseIsoDate(selected))}</h2>
    ${dayEvents.length
      ? `<ul class="agenda-list">${dayEvents.map(eventItemHtml).join('')}</ul>`
      : '<p class="muted">Keine Termine an diesem Tag.</p>'}
    <button type="button" class="btn btn-secondary btn-sm" data-plan-day>Gespräch an diesem Tag planen</button>`;
}

function renderAgenda() {
  const monthEvents = events.filter((e) => inShownMonth(e.date));
  if (monthEvents.length === 0) {
    agenda.innerHTML = `<p class="card agenda-empty">Keine Termine im ${monthLabel.format(month)}.</p>`;
    return;
  }

  const today = todayIso();
  const days = [...new Set(monthEvents.map((e) => e.date))]; // already in date order
  agenda.innerHTML = days.map((iso) => `
    <section class="card agenda-day${iso === today ? ' is-today' : ''}">
      <h3 class="agenda-heading">${agendaDay.format(parseIsoDate(iso))}${iso === today ? ' · Heute' : ''}</h3>
      <ul class="agenda-list">${monthEvents.filter((e) => e.date === iso).map(eventItemHtml).join('')}</ul>
    </section>`).join('');
}

for (const container of [dayPanel, agenda]) {
  container.addEventListener('click', (event) => {
    const item = event.target.closest('[data-application]');
    if (item) openApplication(Number(item.dataset.application));
    if (event.target.closest('[data-plan-day]')) planInterview(selected);
  });
}

// ---------- Actions ----------

function openApplication(id) {
  openDetailPanel(id, { tab: 'schedule', onChanged: load, onDeleted: load });
}

async function planInterview(isoDate) {
  let applications;
  try {
    applications = await api.getApplications();
  } catch (error) {
    showToast(error.message, 'error');
    return;
  }
  if (applications.length === 0) {
    showToast('Legen Sie zuerst eine Bewerbung an.', 'error');
    return;
  }
  openInterviewForm({
    applications,
    defaultDate: isoDate,
    onSaved: async (interview) => {
      showToast('Gespräch gespeichert');
      await showDay(interview.scheduledAt.slice(0, 10));
    },
  });
}

function changeMonth(offset) {
  month = new Date(month.getFullYear(), month.getMonth() + offset, 1);
  const today = todayIso();
  selected = inShownMonth(today) ? today : toIsoDate(month);
  load();
}

document.getElementById('prev-month').addEventListener('click', () => changeMonth(-1));
document.getElementById('next-month').addEventListener('click', () => changeMonth(1));
// Jumps to a day's month, reloads the events and selects the day.
async function showDay(iso) {
  month = firstOfMonth(parseIsoDate(iso));
  selected = iso;
  await load();
  grid.querySelector(`[data-date="${iso}"] .cal-date`)?.focus();
}

document.getElementById('today').addEventListener('click', () => showDay(todayIso()));
document.getElementById('plan-interview').addEventListener('click', () => planInterview(selected));

load();
