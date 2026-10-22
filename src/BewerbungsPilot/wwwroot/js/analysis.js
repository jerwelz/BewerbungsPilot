// Page script for analyse.html. All counting happens on the server (AnalysisService.cs);
// this file displays the result. The drill-down filters the SAME response, so a milestone's
// list always contains exactly the applications in its count.

import { api } from './api.js';
import {
  renderSidebar, statusBadge, statusLabel, escapeHtml, formatDate, formatDateTime, formatLocalDateTime,
  labelOf, INTERVIEW_STATUSES, toIsoDate, todayIso,
} from './ui.js';
import { openDetailPanel } from './detail-panel.js';

renderSidebar('analysis');

const MILESTONES = {
  Submitted: {
    label: 'Bewerbung eingereicht',
    accent: 'Beworben',
    rule: 'Bewerbungsdatum eingetragen. Das ist die Grundlage der Auswertung – deshalb zählen hier alle Bewerbungen des Zeitraums.',
  },
  Interview: {
    label: 'Vorstellungsgespräch erreicht',
    accent: 'Vorstellungsgespraech',
    rule: 'Status „Vorstellungsgespräch“ erfasst oder ein nicht abgesagtes Gespräch eingetragen. Mehrere Gespräche zählen einmal.',
  },
  Offer: {
    label: 'Angebot erhalten',
    accent: 'Angebot',
    rule: 'Status „Angebot“ erfasst – auch wenn danach eine Absage folgte. Ein Angebot ergibt kein Gespräch.',
  },
};

const state = { period: 'all', from: '', to: '', cutoff: todayIso(), selected: null };
let data = null;

const el = {
  period: document.getElementById('an-period'),
  from: document.getElementById('an-from'),
  to: document.getElementById('an-to'),
  cutoff: document.getElementById('an-cutoff'),
  error: document.getElementById('an-error'),
  context: document.getElementById('an-context'),
  stats: document.getElementById('an-stats'),
  milestones: document.getElementById('an-milestones'),
  notes: document.getElementById('an-notes'),
  drill: document.getElementById('an-drill'),
};

el.cutoff.value = state.cutoff;
el.cutoff.max = state.cutoff;

// ---------- Period ----------

function periodRange() {
  const today = new Date();
  if (state.period === '30' || state.period === '90') {
    const from = new Date();
    from.setDate(from.getDate() - Number(state.period));
    return { from: toIsoDate(from), to: toIsoDate(today) };
  }
  if (state.period === 'year') return { from: `${today.getFullYear()}-01-01`, to: toIsoDate(today) };
  if (state.period === 'custom') return { from: state.from, to: state.to };
  return { from: '', to: '' };
}

function periodText(from, to) {
  if (from && to) return `vom ${formatDate(from)} bis ${formatDate(to)}`;
  if (from) return `ab ${formatDate(from)}`;
  if (to) return `bis ${formatDate(to)}`;
  return 'aus dem gesamten Zeitraum';
}

// ---------- Loading ----------

async function load() {
  const { from, to } = periodRange();
  el.error.textContent = '';
  el.milestones.innerHTML = '<p class="tl-state">Analyse wird geladen …</p>';
  try {
    data = await api.getAnalysis({ from, to, cutoff: state.cutoff });
  } catch (error) {
    data = null;
    el.milestones.innerHTML = `<div class="tl-state is-error"><p>Die Analyse konnte nicht geladen werden.</p>
      <button type="button" class="btn btn-secondary btn-sm" data-retry>Erneut versuchen</button></div>`;
    el.error.textContent = Object.values(error.errors ?? {}).flat().join(' ') || error.message;
    el.stats.innerHTML = '';
    el.notes.innerHTML = '';
    el.context.textContent = '';
    el.drill.hidden = true;
    return;
  }
  render();
}

// ---------- Overview ----------

function render() {
  const n = data.cohortSize;
  el.context.innerHTML = `Ausgewertet: <strong>${n === 1 ? '1 Bewerbung' : `${n} Bewerbungen`}</strong> mit eingetragenem
    Bewerbungsdatum ${periodText(data.from, data.to)} · Auswertungsstand: <strong>${formatDate(data.cutoffDate)}</strong>
    (Einträge, die bis zum Ende dieses Tages erfasst wurden)`;

  const o = data.outcomes;
  const stat = (value, label, hint) => `<div class="card analysis-stat"><p class="kpi-value">${value}</p>
    <p class="kpi-label">${label}</p>${hint ? `<p class="kpi-hint">${hint}</p>` : ''}</div>`;
  el.stats.innerHTML = [
    stat(n, 'Bewerbungen im Zeitraum', 'mit Bewerbungsdatum'),
    stat(o.open, 'Noch offen', 'Status zum Auswertungsstand ohne Angebot oder Absage'),
    stat(o.offer + o.rejected, 'Abgeschlossen', `${o.offer} mit Angebot · ${o.rejected} mit Absage`),
    stat(data.incompleteHistoryCount, 'Verlauf unvollständig', 'Statusverlauf mit Lücken erfasst'),
  ].join('');

  if (n === 0) {
    el.milestones.innerHTML = `<p class="tl-state">Im gewählten Zeitraum gibt es keine Bewerbungen mit eingetragenem
      Bewerbungsdatum. Wählen Sie einen anderen Zeitraum oder tragen Sie bei Ihren Bewerbungen das Bewerbungsdatum ein.</p>`;
  } else {
    el.milestones.innerHTML = data.milestones.map((m) => milestoneHtml(m, n)).join('');
  }
  renderNotes();

  // Keep the drill-down open for the new data (same cohort as the counts above).
  if (state.selected) renderDrill(false);
  else el.drill.hidden = true;
}

function milestoneHtml(m, n) {
  const info = MILESTONES[m.milestone];
  const share = n > 0 ? (m.count / n) * 100 : 0;
  const without = n - m.count;
  const percent = m.percent !== null ? ` <span class="milestone-percent">(${m.percent} %)</span>` : '';
  return `
    <button type="button" class="milestone${state.selected === m.milestone ? ' is-selected' : ''}" data-milestone="${m.milestone}"
      aria-pressed="${state.selected === m.milestone}" aria-describedby="rule-${m.milestone}">
      <span class="milestone-head">
        <span class="milestone-label">${info.label}</span>
        <span class="milestone-count"><strong>${m.count}</strong> von ${n}${percent}</span>
      </span>
      <span class="milestone-track" aria-hidden="true">
        <span class="milestone-fill accent-${info.accent}" style="width:${share}%"></span>
      </span>
      <span class="milestone-meta">
        ${m.milestone === 'Submitted' ? '' : `<span>${without} ohne dokumentierten Nachweis</span>`}
        <span class="milestone-rule" id="rule-${m.milestone}">${info.rule}</span>
      </span>
    </button>`;
}

function renderNotes() {
  const w = data.withoutSubmissionDate;
  const notes = [];
  if (data.cohortSize > 0 && data.cohortSize < data.minCohortForPercent) {
    notes.push(`Bei weniger als ${data.minCohortForPercent} Bewerbungen werden keine Prozentwerte angezeigt – nur absolute Zahlen.`);
  }
  if (data.incompleteHistoryCount > 0) {
    notes.push(`Bei ${data.incompleteHistoryCount === 1 ? 'einer Bewerbung' : `${data.incompleteHistoryCount} Bewerbungen`} ist der Statusverlauf
      unvollständig erfasst. Fehlende Einträge bedeuten nicht, dass ein Meilenstein nicht erreicht wurde.`);
  }
  const total = w.drafts + w.other;
  if (total > 0) {
    notes.push(`<button type="button" class="link-button" data-milestone="WithoutDate" aria-pressed="${state.selected === 'WithoutDate'}">
      ${total === 1 ? '1 Bewerbung' : `${total} Bewerbungen`} ohne Bewerbungsdatum</button>
      ${w.drafts ? `(${w.drafts} ${w.drafts === 1 ? 'Entwurf' : 'Entwürfe'}${w.other ? `, ${w.other} mit anderem Status` : ''})` : `(${w.other} mit anderem Status als Entwurf)`}
      ${total === 1 ? 'ist' : 'sind'} nicht in der Auswertung enthalten.`);
  }
  el.notes.innerHTML = notes.map((text) => `<p>${text}</p>`).join('');
}

// ---------- Drill-down ----------

function evidenceText(e) {
  switch (e.source) {
    case 'SubmissionDate': return `Bewerbungsdatum eingetragen: ${formatDate(e.date)}`;
    case 'StatusRecorded': return `Status „${statusLabel(e.status)}“ erfasst am ${formatDateTime(e.recordedAt)}`;
    case 'CurrentStatus': return `Aktueller Status „${statusLabel(e.status)}“, erfasst am ${formatDateTime(e.recordedAt)} (kein Eintrag im Verlauf)`;
    case 'InterviewRecorded': return `Gespräch am ${formatLocalDateTime(e.appointmentAt)} (${labelOf(INTERVIEW_STATUSES, e.interviewStatus)}) – eingetragen am ${formatDateTime(e.recordedAt)}`;
    default: return '';
  }
}

function renderDrill(moveFocus) {
  const isWithoutDate = state.selected === 'WithoutDate';
  const milestone = data.milestones.find((m) => m.milestone === state.selected);
  // Exactly the counted applications: the ids the server counted.
  const items = isWithoutDate
    ? data.withoutSubmissionDate.applications
    : data.applications.filter((a) => milestone.applicationIds.includes(a.id));
  const title = isWithoutDate ? 'Ohne Bewerbungsdatum' : MILESTONES[state.selected].label;
  const count = items.length === 1 ? '1 Bewerbung' : `${items.length} Bewerbungen`;

  const list = items.length === 0
    ? `<p class="tl-state">Keine Bewerbung mit dokumentiertem Nachweis${isWithoutDate ? '' : ' für diesen Meilenstein'}.</p>`
    : `<ul class="drill-list">${items.map((a) => drillItemHtml(a, isWithoutDate)).join('')}</ul>`;

  el.drill.hidden = false;
  el.drill.innerHTML = `
    <header class="drill-header">
      <div>
        <h2 class="card-title" id="an-drill-title" tabindex="-1">${title} · ${count}</h2>
        <p class="card-subtitle">${isWithoutDate
          ? 'Nicht in der Auswertung, weil kein Bewerbungsdatum eingetragen ist.'
          : 'Genau die Bewerbungen, die oben gezählt werden – mit ihren Nachweisen.'}</p>
      </div>
      <button type="button" class="btn btn-secondary btn-sm" data-back>Zurück zur Übersicht</button>
    </header>
    ${list}`;
  if (moveFocus) el.drill.querySelector('#an-drill-title').focus();
}

function drillItemHtml(a, isWithoutDate) {
  const evidence = isWithoutDate ? [] : a.evidence.filter((e) => e.milestone === state.selected);
  const atCutoff = a.statusAtCutoff !== a.currentStatus
    ? `<span class="drill-cutoff">Zum Auswertungsstand: ${a.statusAtCutoff ? statusLabel(a.statusAtCutoff) : 'noch nicht erfasst'}</span>` : '';
  return `
    <li class="drill-item">
      <div class="drill-main">
        <div class="drill-company">${escapeHtml(a.companyName)}</div>
        <div class="drill-position">${escapeHtml(a.position)}${a.appliedOn ? ` · Bewerbungsdatum ${formatDate(a.appliedOn)}` : ' · kein Bewerbungsdatum'}</div>
        ${evidence.length ? `<ul class="drill-evidence">${evidence.map((e) => `<li>${evidenceText(e)}</li>`).join('')}</ul>` : ''}
      </div>
      <div class="drill-side">
        ${statusBadge(a.currentStatus)}
        ${atCutoff}
        ${a.historyComplete ? '' : '<span class="drill-flag">Verlauf unvollständig</span>'}
        <button type="button" class="btn btn-ghost btn-sm" data-open="${a.id}">Details öffnen</button>
      </div>
    </li>`;
}

function select(key) {
  state.selected = state.selected === key ? null : key;
  render();
  if (state.selected) renderDrill(true);
  else el.milestones.querySelector(`[data-milestone="${key}"]`)?.focus();
}

function back() {
  const previous = state.selected;
  state.selected = null;
  render();
  document.querySelector(`[data-milestone="${previous}"]`)?.focus();
}

// ---------- Events ----------

document.querySelector('.analysis-card').addEventListener('click', (event) => {
  const button = event.target.closest('[data-milestone]');
  if (button) select(button.dataset.milestone);
  if (event.target.closest('[data-retry]')) load();
});

el.drill.addEventListener('click', (event) => {
  if (event.target.closest('[data-back]')) back();
  const open = event.target.closest('[data-open]');
  if (open) openDetailPanel(Number(open.dataset.open), { onChanged: load, onDeleted: load, tab: 'history' });
});
el.drill.addEventListener('keydown', (event) => {
  if (event.key === 'Escape' && !document.querySelector('dialog[open]')) back();
});

el.period.addEventListener('input', () => {
  state.period = el.period.value;
  document.querySelectorAll('[data-custom]').forEach((f) => { f.hidden = state.period !== 'custom'; });
  if (state.period !== 'custom' || (state.from && state.to)) load();
});
for (const input of [el.from, el.to]) {
  input.addEventListener('change', () => {
    state.from = el.from.value;
    state.to = el.to.value;
    if (state.from && state.to) load();
  });
}
el.cutoff.addEventListener('change', () => {
  state.cutoff = el.cutoff.value || todayIso();
  load();
});

load();
