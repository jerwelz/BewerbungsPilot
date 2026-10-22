// Page script for verlauf.html: the recorded status history of all applications
// on a shared horizontal time axis (desktop) or as cards (narrow screens).
//
// The server (TimelineService.cs) reconstructs the history; this file only places it:
// - "Kalenderzeit": position = point in time (all applications on one calendar axis)
// - "Tage seit Bewerbung": position = days since the entered submission date (Day 0),
//   values calculated on the server. Applications without submission date are left out.

import { api } from './api.js';
import {
  renderSidebar, STATUSES, statusLabel, statusBadge, escapeHtml, formatDate, formatDateTime, formatLocalDateTime,
  matchesSearch, labelOf, INTERVIEW_TYPES, INTERVIEW_STATUSES, toIsoDate,
} from './ui.js';
import { openDetailPanel } from './detail-panel.js';

renderSidebar('timeline');

const DAY = 86_400_000;
const state = {
  mode: 'calendar',      // 'calendar' | 'day0'
  range: 'all',
  search: '',
  status: '',
  sort: 'applied-desc',
  selectedId: null,
  compare: new Set(),    // ids marked for comparison
  compareOnly: false,
};
let data = null;         // TimelineResponse from the server

const el = {
  body: document.getElementById('tl-body'),
  cards: document.getElementById('journey-cards'),
  detail: document.getElementById('tl-detail'),
  tooltip: document.getElementById('tl-tooltip'),
  notice: document.getElementById('tl-notice'),
  compareBar: document.getElementById('tl-compare-bar'),
  search: document.getElementById('tl-search'),
  status: document.getElementById('tl-status'),
  range: document.getElementById('tl-range'),
  sort: document.getElementById('tl-sort'),
};

const shortDate = new Intl.DateTimeFormat('de-DE', { day: '2-digit', month: '2-digit' });
const monthShort = new Intl.DateTimeFormat('de-DE', { month: 'short' });
const monthYear = new Intl.DateTimeFormat('de-DE', { month: 'short', year: 'numeric' });
const dateOnly = new Intl.DateTimeFormat('de-DE', { day: '2-digit', month: '2-digit', year: 'numeric' });

// ---------- Small helpers ----------

// "2026-10-01" → local midnight in ms (never Date.parse, which would mean UTC midnight)
function localDay(iso) {
  const [y, m, d] = iso.split('-').map(Number);
  return new Date(y, m - 1, d).getTime();
}

const instant = (utcText) => Date.parse(utcText);             // "…Z" recording times
const localTime = (text) => new Date(text).getTime();          // interview wall-clock time

function formatDuration(days) {
  if (days < 1) return 'unter 1 Tag';
  const rounded = Math.round(days);
  return rounded === 1 ? '1 Tag' : `${rounded} Tage`;
}

// Day index: 0.5 days after submission is still "Tag 0"; negative = before submission.
function dayLabel(day) {
  const index = Math.floor(day);
  return index < 0 ? `Tag ${index.toString().replace('-', '−')} (vor der Bewerbung)` : `Tag ${index}`;
}

const interviewLabel = (i) => `Vorstellungsgespräch (${labelOf(INTERVIEW_TYPES, i.type)})`;

// ---------- Loading ----------

async function load() {
  el.body.innerHTML = '<p class="tl-state">Verlauf wird geladen …</p>';
  let from;
  let to;
  if (state.mode === 'calendar' && state.range !== 'all') {
    const start = new Date();
    start.setDate(start.getDate() - Number(state.range));
    from = toIsoDate(start);
    to = toIsoDate(new Date());
  }
  try {
    data = await api.getTimeline(from, to);
  } catch (error) {
    data = null;
    el.body.innerHTML = `<div class="tl-state is-error"><p>Der Verlauf konnte nicht geladen werden: ${escapeHtml(error.message)}</p>
      <button type="button" class="btn btn-secondary btn-sm" data-retry>Erneut versuchen</button></div>`;
    el.cards.innerHTML = '';
    el.detail.hidden = true;
    return;
  }
  render();
}

// ---------- Filtering & sorting ----------

const lastChange = (j) => Math.max(0, ...j.transitions.map((t) => instant(t.recordedAt)));
const firstStart = (j) => Math.min(...j.segments.map((s) => instant(s.start)));
const appliedValue = (j) => (j.appliedOn ? localDay(j.appliedOn) : null);
const nullsLast = (a, b, compare) => (a === null) - (b === null) || (a === null ? 0 : compare(a, b));

const SORTERS = {
  'applied-desc': (a, b) => nullsLast(appliedValue(a), appliedValue(b), (x, y) => y - x),
  'applied-asc': (a, b) => nullsLast(appliedValue(a), appliedValue(b), (x, y) => x - y),
  changed: (a, b) => lastChange(b) - lastChange(a),
  duration: (a, b) => firstStart(a) - firstStart(b),
  company: (a, b) => a.companyName.localeCompare(b.companyName, 'de', { sensitivity: 'base' }),
};

// Returns { shown, withoutDate } – withoutDate counts applications hidden in Day-0 mode.
function visibleJourneys() {
  let list = data.applications
    .filter((j) => matchesSearch(j, state.search))
    .filter((j) => !state.status || j.currentStatus === state.status);
  if (state.compareOnly && state.compare.size > 0) list = list.filter((j) => state.compare.has(j.id));

  let withoutDate = 0;
  if (state.mode === 'day0') {
    withoutDate = list.filter((j) => !j.appliedOn).length;
    list = list.filter((j) => j.appliedOn);
  }
  return { shown: [...list].sort(SORTERS[state.sort]), withoutDate };
}

// ---------- Coordinates for the active mode ----------

// Everything a journey contains, as numbers on the active axis (ms or days).
function coordinates(j) {
  const now = instant(data.generatedAt);
  if (state.mode === 'calendar') {
    return {
      segment: (s) => [instant(s.start), s.end ? instant(s.end) : now],
      transition: (t) => instant(t.recordedAt),
      interview: (i) => localTime(i.scheduledAt),
      applied: j.appliedOn ? localDay(j.appliedOn) : null,
      deadline: j.deadline ? localDay(j.deadline) : null,
    };
  }
  return {
    segment: (s) => [s.startDay, s.endDay ?? j.currentDay],
    transition: (t) => t.day,
    interview: (i) => i.day,
    applied: 0,
    deadline: j.deadlineDay,
  };
}

function computeAxis(journeys) {
  if (state.mode === 'calendar' && data.rangeStart) {
    return { min: instant(data.rangeStart), max: instant(data.rangeEnd) };
  }
  let min = Infinity;
  let max = state.mode === 'calendar' ? instant(data.generatedAt) : 0;
  for (const j of journeys) {
    const c = coordinates(j);
    const values = [
      ...j.segments.flatMap(c.segment),
      ...j.interviews.map(c.interview),
      c.applied, c.deadline,
    ].filter((v) => v !== null && v !== undefined);
    min = Math.min(min, ...values);
    max = Math.max(max, ...values);
  }
  if (!Number.isFinite(min)) min = state.mode === 'calendar' ? max - 30 * DAY : -1;
  const unit = state.mode === 'calendar' ? DAY : 1;
  const pad = Math.max((max - min) * 0.03, unit);
  return { min: min - pad, max: max + pad };
}

const percent = (axis, value) => ((value - axis.min) / (axis.max - axis.min)) * 100;
const inAxis = (axis, value) => value !== null && value !== undefined && value >= axis.min && value <= axis.max;

function ticks(axis) {
  const result = [];
  if (state.mode === 'day0') {
    const span = axis.max - axis.min;
    const step = [1, 2, 7, 14, 30, 60, 90, 180].find((s) => span / s <= 10) ?? 365;
    for (let d = Math.ceil(axis.min / step) * step; d <= axis.max; d += step) {
      result.push({ value: d, label: d === 0 ? 'Tag 0 · Bewerbung' : `Tag ${d.toString().replace('-', '−')}`, strong: d === 0 });
    }
    if (!result.some((t) => t.value === 0) && inAxis(axis, 0)) result.push({ value: 0, label: 'Tag 0 · Bewerbung', strong: true });
    return result;
  }

  // Calendar: weekly (short spans), monthly or quarterly – always at local midnight.
  const spanDays = (axis.max - axis.min) / DAY;
  const start = new Date(axis.min);
  if (spanDays <= 60) {
    const day = new Date(start.getFullYear(), start.getMonth(), start.getDate());
    day.setDate(day.getDate() + ((8 - day.getDay()) % 7)); // next Monday
    for (; day.getTime() <= axis.max; day.setDate(day.getDate() + 7)) {
      result.push({ value: day.getTime(), label: shortDate.format(day) });
    }
  } else {
    const stepMonths = spanDays <= 550 ? 1 : 3;
    const month = new Date(start.getFullYear(), start.getMonth() + 1, 1);
    for (; month.getTime() <= axis.max; month.setMonth(month.getMonth() + stepMonths)) {
      const label = month.getMonth() === 0 || result.length === 0 ? monthYear.format(month) : monthShort.format(month);
      result.push({ value: month.getTime(), label });
    }
  }
  return result;
}

// ---------- Markers (shared by timeline, tooltip and screen readers) ----------

function markers(j) {
  const c = coordinates(j);
  const list = [];
  if (j.appliedOn) {
    list.push({ kind: 'submitted', value: c.applied, text: `Bewerbung versendet: ${formatDate(j.appliedOn)} (eingetragenes Datum)` });
  }
  j.transitions.filter((t) => !t.initial).forEach((t) => list.push({
    kind: 'transition',
    backward: t.backward,
    value: c.transition(t),
    text: `Status erfasst am ${formatDateTime(t.recordedAt)}: ${statusLabel(t.from)} → ${statusLabel(t.to)}${t.backward ? ' (Rückschritt)' : ''}`,
  }));
  j.interviews.forEach((i) => list.push({
    kind: 'interview',
    cancelled: i.status === 'Abgesagt',
    value: c.interview(i),
    text: `${interviewLabel(i)}: Termin ${formatLocalDateTime(i.scheduledAt)} · ${labelOf(INTERVIEW_STATUSES, i.status)}`,
  }));
  if (j.deadline) {
    list.push({ kind: 'deadline', value: c.deadline, text: `Bewerbungsfrist: ${formatDate(j.deadline)}` });
  }
  return list;
}

// ---------- Rendering: desktop timeline ----------

function render() {
  hideTooltip();
  const { shown, withoutDate } = visibleJourneys();

  el.notice.hidden = withoutDate === 0;
  el.notice.textContent = withoutDate === 1
    ? '1 Bewerbung ohne eingetragenes Bewerbungsdatum ist in dieser Ansicht ausgeblendet – ein Tag 0 wird nicht erfunden.'
    : `${withoutDate} Bewerbungen ohne eingetragenes Bewerbungsdatum sind in dieser Ansicht ausgeblendet – ein Tag 0 wird nicht erfunden.`;
  el.range.disabled = state.mode === 'day0';
  el.range.title = state.mode === 'day0' ? 'Der Zeitraum gilt nur für die Kalenderzeit.' : '';
  renderCompareBar();
  renderLegend();

  if (data.applications.length === 0) {
    const empty = '<p class="tl-state">Noch keine Bewerbungen. Sobald Sie Bewerbungen anlegen und ihren Status ändern, entsteht hier ihr Verlauf.</p>';
    el.body.innerHTML = empty;
    el.cards.innerHTML = `<div class="card">${empty}</div>`;
    renderDetail(null);
    return;
  }
  if (shown.length === 0) {
    const empty = `<div class="tl-state"><p>Keine Bewerbungen passen zu Suche, Filter und Zeitraum.</p>
      <button type="button" class="btn btn-secondary btn-sm" data-reset>Filter zurücksetzen</button></div>`;
    el.body.innerHTML = empty;
    el.cards.innerHTML = `<div class="card">${empty}</div>`;
    renderDetail(null);
    return;
  }

  const axis = computeAxis(shown);
  const tickList = ticks(axis);
  const now = instant(data.generatedAt);
  const todayLine = state.mode === 'calendar' && inAxis(axis, now)
    ? `<div class="tl-today" style="left:${percent(axis, now)}%"><span>Heute</span></div>` : '';

  el.body.innerHTML = `
    <div class="tl-grid">
      <div class="tl-axis">
        <div class="tl-axis-label">${state.mode === 'calendar' ? 'Bewerbung' : 'Bewerbung · Tage seit Bewerbung'}</div>
        <div class="tl-axis-track">
          ${tickList.map((t) => `<span class="tl-tick${t.strong ? ' is-strong' : ''}" style="left:${percent(axis, t.value)}%">${t.label}</span>`).join('')}
        </div>
      </div>
      <div class="tl-rows" role="list" aria-label="Bewerbungen im Zeitverlauf">
        <div class="tl-gridlines" aria-hidden="true">
          ${tickList.map((t) => `<span class="${t.strong ? 'is-strong' : ''}" style="left:${percent(axis, t.value)}%"></span>`).join('')}
          ${todayLine}
        </div>
        ${shown.map((j) => rowHtml(j, axis)).join('')}
      </div>
    </div>`;

  el.cards.innerHTML = shown.map(cardHtml).join('');

  if (state.selectedId && !shown.some((j) => j.id === state.selectedId)) state.selectedId = null;
  renderDetail(shown.find((j) => j.id === state.selectedId) ?? null);
}

function rowHtml(j, axis) {
  const c = coordinates(j);
  const selected = j.id === state.selectedId;
  const segments = j.segments.map((s) => {
    const [start, end] = c.segment(s);
    if (end < axis.min || start > axis.max) return '';
    const left = Math.max(0, percent(axis, start));
    const right = Math.min(100, percent(axis, end));
    const width = Math.max(right - left, 0.5);
    const label = statusLabel(s.status);
    return `<div class="tl-seg accent-${s.status}${s.uncertain ? ' is-uncertain' : ''}${s.end ? '' : ' is-current'}"
      style="left:${left}%;width:${width}%" aria-hidden="true">${width > 9 ? `<span>${escapeHtml(label)}</span>` : ''}</div>`;
  }).join('');

  const markerHtml = markers(j)
    .filter((m) => inAxis(axis, m.value))
    .map((m) => `<button type="button" class="tl-marker marker-${m.kind}${m.backward ? ' is-backward' : ''}${m.cancelled ? ' is-cancelled' : ''}"
      style="left:${percent(axis, m.value)}%" aria-label="${escapeHtml(m.text)}" data-tip="${escapeHtml(m.text)}"
      tabindex="${selected ? 0 : -1}"></button>`)
    .join('');

  return `
    <div class="tl-row${selected ? ' is-selected' : ''}${state.compare.has(j.id) ? ' is-compared' : ''}" role="listitem" data-id="${j.id}">
      <div class="tl-label">
        <input type="checkbox" class="tl-compare" data-compare="${j.id}" ${state.compare.has(j.id) ? 'checked' : ''}
          aria-label="${escapeHtml(j.companyName)} zum Vergleich markieren">
        <button type="button" class="tl-select" data-select="${j.id}" aria-pressed="${selected}" aria-describedby="tl-sum-${j.id}">
          <span class="tl-company">${escapeHtml(j.companyName)}</span>
          <span class="tl-position">${escapeHtml(j.position)}</span>
        </button>
        <span class="visually-hidden" id="tl-sum-${j.id}">${escapeHtml(summary(j))}</span>
      </div>
      <div class="tl-track">${segments}${markerHtml}</div>
    </div>`;
}

// Text version of a row for screen readers.
function summary(j) {
  const parts = j.segments.map((s) => {
    const end = s.end ? instant(s.end) : instant(data.generatedAt);
    return `${statusLabel(s.status)} ${formatDuration((end - instant(s.start)) / DAY)}${s.end ? '' : ' (aktuell)'}`;
  });
  return `Aktueller Status ${statusLabel(j.currentStatus)}. Erfasster Verlauf: ${parts.join(', ')}.${j.historyComplete ? '' : ' Verlauf unvollständig erfasst.'}`;
}

function renderLegend() {
  const statuses = STATUSES.map((s) => `<li><span class="tl-swatch accent-${s.value}" aria-hidden="true"></span>${s.label}</li>`).join('');
  document.getElementById('tl-legend').innerHTML = `${statuses}
    <li><span class="tl-swatch is-uncertain-swatch" aria-hidden="true"></span>unvollständig erfasst</li>
    <li><span class="tl-legend-marker marker-transition" aria-hidden="true"></span>Statuswechsel (erfasst)</li>
    <li><span class="tl-legend-marker marker-transition is-backward" aria-hidden="true"></span>Rückschritt</li>
    <li><span class="tl-legend-marker marker-submitted" aria-hidden="true"></span>Bewerbung versendet</li>
    <li><span class="tl-legend-marker marker-interview" aria-hidden="true"></span>Gespräch (Termin)</li>
    <li><span class="tl-legend-marker marker-deadline" aria-hidden="true"></span>Bewerbungsfrist</li>`;
}

function renderCompareBar() {
  const count = state.compare.size;
  el.compareBar.hidden = count === 0;
  if (count === 0) {
    state.compareOnly = false;
    return;
  }
  el.compareBar.innerHTML = `
    <span>${count === 1 ? '1 Bewerbung' : `${count} Bewerbungen`} zum Vergleich markiert</span>
    <button type="button" class="btn btn-secondary btn-sm" data-compare-only aria-pressed="${state.compareOnly}">
      ${state.compareOnly ? 'Alle anzeigen' : 'Nur markierte anzeigen'}</button>
    <button type="button" class="btn btn-ghost btn-sm" data-compare-clear>Markierung aufheben</button>`;
}

// ---------- Chronology (detail card and mobile cards) ----------

function entries(j) {
  const list = [];
  if (j.appliedOn) {
    list.push({ at: localDay(j.appliedOn), kind: 'submitted', day: 0, title: 'Bewerbung versendet',
      when: formatDate(j.appliedOn), note: 'eingetragenes Bewerbungsdatum' });
  }
  for (const t of j.transitions) {
    list.push({
      at: instant(t.recordedAt), kind: t.initial ? 'created' : 'transition', day: t.day, backward: t.backward,
      title: t.initial ? `Bewerbung angelegt mit Status „${statusLabel(t.to)}“` : `Status: ${statusLabel(t.from)} → ${statusLabel(t.to)}`,
      when: formatDateTime(t.recordedAt),
      note: t.backward ? 'Zeitpunkt der Erfassung · Rückschritt' : 'Zeitpunkt der Erfassung',
    });
  }
  for (const i of j.interviews) {
    list.push({ at: localTime(i.scheduledAt), kind: 'interview', day: i.day, title: interviewLabel(i),
      when: formatLocalDateTime(i.scheduledAt), note: `Termin · ${labelOf(INTERVIEW_STATUSES, i.status)}` });
  }
  if (j.deadline) {
    list.push({ at: localDay(j.deadline), kind: 'deadline', day: j.deadlineDay, title: 'Bewerbungsfrist',
      when: formatDate(j.deadline), note: 'Fälligkeitsdatum' });
  }
  return list.sort((a, b) => a.at - b.at);
}

function entriesHtml(j) {
  return entries(j).map((e) => `
    <li class="entry entry-${e.kind}${e.backward ? ' is-backward' : ''}">
      <span class="entry-marker tl-legend-marker marker-${e.kind === 'created' ? 'transition' : e.kind}${e.backward ? ' is-backward' : ''}" aria-hidden="true"></span>
      <div>
        <div class="entry-title">${e.title}</div>
        <div class="entry-meta">
          ${state.mode === 'day0' && e.day !== null && e.day !== undefined ? `<strong>${dayLabel(e.day)}</strong> · ` : ''}
          <time>${e.when}</time>${state.mode === 'calendar' && e.day !== null && e.day !== undefined ? ` · ${dayLabel(e.day)}` : ''}
          · ${e.note}
        </div>
      </div>
    </li>`).join('');
}

function durationsHtml(j) {
  const now = instant(data.generatedAt);
  const rows = j.segments.map((s) => {
    const start = instant(s.start);
    const end = s.end ? instant(s.end) : now;
    return `
      <li>
        <span class="tl-swatch accent-${s.status}${s.uncertain ? ' is-uncertain' : ''}" aria-hidden="true"></span>
        <span class="duration-status">${statusLabel(s.status)}</span>
        <span class="duration-range">${dateOnly.format(start)} – ${s.end ? dateOnly.format(end) : 'heute'}</span>
        <span class="duration-value">${formatDuration((end - start) / DAY)}${s.end ? '' : ' · aktuell'}</span>
        ${s.uncertain ? '<span class="duration-flag">Beginn nicht genau erfasst</span>' : ''}
      </li>`;
  }).join('');
  const before = j.startsBeforeRange
    ? `<p class="muted">Status zu Beginn des gewählten Zeitraums: <strong>${statusLabel(j.segments[0].status)}</strong>
       (erfasst seit ${dateOnly.format(instant(j.segments[0].start))}).</p>` : '';
  return `${before}<ul class="duration-list">${rows}</ul>`;
}

function renderDetail(j) {
  el.detail.hidden = false;
  if (!j) {
    el.detail.innerHTML = '<p class="tl-state">Wählen Sie eine Bewerbung aus, um ihren erfassten Verlauf im Detail zu sehen.</p>';
    return;
  }
  el.detail.innerHTML = `
    <header class="tl-detail-header">
      <div>
        <h2 class="card-title">${escapeHtml(j.companyName)}</h2>
        <p class="card-subtitle">${escapeHtml(j.position)}</p>
      </div>
      <div class="tl-detail-actions">
        ${statusBadge(j.currentStatus)}
        <button type="button" class="btn btn-secondary btn-sm" data-open="${j.id}">Details öffnen</button>
      </div>
    </header>
    <p class="tl-completeness ${j.historyComplete ? '' : 'is-incomplete'}">
      ${j.historyComplete
        ? 'Statusverlauf lückenlos erfasst.'
        : 'Statusverlauf unvollständig erfasst. Fehlende Abschnitte werden nicht ergänzt; gestreifte Abschnitte haben keinen genau erfassten Beginn.'}
    </p>
    <div class="tl-detail-columns">
      <section aria-labelledby="chronology-heading">
        <h3 class="section-title" id="chronology-heading">Chronologie</h3>
        <ol class="entry-list">${entriesHtml(j)}</ol>
      </section>
      <section aria-labelledby="durations-heading">
        <h3 class="section-title" id="durations-heading">Erfasste Dauer je Status</h3>
        ${durationsHtml(j)}
      </section>
    </div>`;
}

// ---------- Rendering: narrow screens ----------

function cardHtml(j) {
  // Compact bar: each recorded status gets a share of this application's own history.
  const now = instant(data.generatedAt);
  const durations = j.segments.map((s) => (s.end ? instant(s.end) : now) - instant(s.start));
  const total = Math.max(1, durations.reduce((sum, d) => sum + d, 0));
  const bar = j.segments.map((s, i) =>
    `<span class="accent-${s.status}${s.uncertain ? ' is-uncertain' : ''}" style="flex-grow:${Math.max((durations[i] / total) * 100, 2)}"></span>`).join('');

  return `
    <article class="card journey-card${j.id === state.selectedId ? ' is-selected' : ''}" data-id="${j.id}">
      <header class="journey-header">
        <div>
          <h2 class="journey-company">${escapeHtml(j.companyName)}</h2>
          <p class="journey-position">${escapeHtml(j.position)}</p>
        </div>
        ${statusBadge(j.currentStatus)}
      </header>
      <div class="journey-bar" aria-hidden="true">${bar}</div>
      <p class="visually-hidden">${escapeHtml(summary(j))}</p>
      ${j.historyComplete ? '' : '<p class="journey-incomplete">Verlauf unvollständig erfasst</p>'}
      <ol class="entry-list">${entriesHtml(j)}</ol>
      <button type="button" class="btn btn-secondary btn-sm" data-open="${j.id}">Details öffnen</button>
    </article>`;
}

// ---------- Tooltip ----------

function showTooltip(marker) {
  const card = marker.closest('.tl-card').getBoundingClientRect();
  const box = marker.getBoundingClientRect();
  el.tooltip.textContent = marker.dataset.tip;
  el.tooltip.hidden = false;
  const width = el.tooltip.offsetWidth;
  const left = Math.min(Math.max(box.left + box.width / 2 - card.left - width / 2, 8), card.width - width - 8);
  el.tooltip.style.left = `${left}px`;
  el.tooltip.style.top = `${box.top - card.top - el.tooltip.offsetHeight - 8}px`;
}

function hideTooltip() {
  el.tooltip.hidden = true;
}

// ---------- Interaction ----------

function select(id, { focus = false } = {}) {
  state.selectedId = id;
  render();
  if (focus) el.body.querySelector(`[data-select="${id}"]`)?.focus();
}

el.body.addEventListener('click', (event) => {
  const marker = event.target.closest('.tl-marker');
  const row = event.target.closest('.tl-row');
  if (event.target.closest('[data-compare]')) return; // handled by "change"
  if (event.target.closest('[data-retry]')) return load();
  if (event.target.closest('[data-reset]')) return resetFilters();
  if (!row) return;
  const id = Number(row.dataset.id);
  if (state.selectedId !== id) select(id, { focus: !marker });
  if (marker) el.detail.scrollIntoView({ behavior: matchMedia('(prefers-reduced-motion: reduce)').matches ? 'auto' : 'smooth', block: 'nearest' });
});

el.body.addEventListener('change', (event) => {
  const box = event.target.closest('[data-compare]');
  if (!box) return;
  const id = Number(box.dataset.compare);
  if (box.checked) state.compare.add(id);
  else state.compare.delete(id);
  render();
  el.body.querySelector(`[data-compare="${id}"]`)?.focus();
});

// Arrow keys move between rows (and select them); Home/End jump to the first/last row.
el.body.addEventListener('keydown', (event) => {
  const current = event.target.closest('[data-select]');
  if (!current) return;
  const buttons = [...el.body.querySelectorAll('[data-select]')];
  const index = buttons.indexOf(current);
  const next = { ArrowDown: index + 1, ArrowUp: index - 1, Home: 0, End: buttons.length - 1 }[event.key];
  if (next === undefined || !buttons[next]) return;
  event.preventDefault();
  select(Number(buttons[next].dataset.select), { focus: true });
});

el.body.addEventListener('pointerover', (event) => {
  const marker = event.target.closest('.tl-marker');
  if (marker) showTooltip(marker);
});
el.body.addEventListener('pointerout', (event) => {
  if (event.target.closest('.tl-marker')) hideTooltip();
});
el.body.addEventListener('focusin', (event) => {
  const marker = event.target.closest('.tl-marker');
  if (marker) showTooltip(marker);
  else hideTooltip();
});
el.body.addEventListener('focusout', hideTooltip);

for (const container of [el.detail, el.cards]) {
  container.addEventListener('click', (event) => {
    const open = event.target.closest('[data-open]');
    if (open) openDetailPanel(Number(open.dataset.open), { onChanged: load, onDeleted: load, tab: 'history' });
    if (event.target.closest('[data-reset]')) resetFilters();
  });
}

el.compareBar.addEventListener('click', (event) => {
  if (event.target.closest('[data-compare-only]')) state.compareOnly = !state.compareOnly;
  else if (event.target.closest('[data-compare-clear]')) {
    state.compare.clear();
    state.compareOnly = false;
  } else return;
  render();
});

document.querySelectorAll('[data-mode]').forEach((button) => button.addEventListener('click', () => {
  if (state.mode === button.dataset.mode) return;
  state.mode = button.dataset.mode;
  document.querySelectorAll('[data-mode]').forEach((b) => b.setAttribute('aria-pressed', String(b === button)));
  // Day-0 mode always uses the complete history; the range only applies to calendar time.
  if (state.range !== 'all') load();
  else render();
}));

el.status.insertAdjacentHTML('beforeend', STATUSES.map((s) => `<option value="${s.value}">${escapeHtml(s.label)}</option>`).join(''));
el.search.addEventListener('input', () => { state.search = el.search.value; if (data) render(); });
el.status.addEventListener('input', () => { state.status = el.status.value; if (data) render(); });
el.sort.addEventListener('input', () => { state.sort = el.sort.value; if (data) render(); });
el.range.addEventListener('input', () => { state.range = el.range.value; load(); });

function resetFilters() {
  state.search = '';
  state.status = '';
  state.compareOnly = false;
  el.search.value = '';
  el.status.value = '';
  if (state.range !== 'all') {
    state.range = 'all';
    el.range.value = 'all';
    load();
  } else {
    render();
  }
}

load();
