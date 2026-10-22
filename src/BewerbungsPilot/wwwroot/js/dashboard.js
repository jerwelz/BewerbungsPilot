// Page script for index.html (Übersicht). All numbers come from GET /api/dashboard,
// which calculates them in DashboardService.cs. This file only displays them.

import { api } from './api.js';
import {
  renderSidebar, statusLabel, statusBadge, escapeHtml, formatDateTime, showToast, formatDate, formatTime, statePill, labelOf, INTERVIEW_TYPES,
} from './ui.js';
import { dateBlock } from './schedule-tab.js';
import { openApplicationForm } from './application-form.js';

renderSidebar('overview');

// One restrained color per status – the same hues as the status badges.
const STATUS_COLORS = {
  Entwurf: '#94A3B8',
  Beworben: '#2563EB',
  InPruefung: '#D97706',
  Vorstellungsgespraech: '#7C3AED',
  Angebot: '#059669',
  Absage: '#DC2626',
};

const reducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
const charts = {}; // existing Chart.js instances, destroyed before redrawing

document.getElementById('dashboard-date').textContent =
  `Stand: ${new Intl.DateTimeFormat('de-DE', { dateStyle: 'full' }).format(new Date())}`;

document.getElementById('new-application').addEventListener('click', () => {
  openApplicationForm({
    onSaved: () => {
      showToast('Bewerbung gespeichert');
      loadDashboard();
    },
  });
});

async function loadDashboard() {
  try {
    const [data, upcoming] = await Promise.all([api.getDashboard(), api.getUpcoming()]);
    renderKpis(data);
    renderActivityChart(data.monthlyActivity);
    renderStatusChart(data);
    renderUpcoming(upcoming);
    renderRecentActivity(data.recentActivity);
    await loadWaiting();
  } catch (error) {
    document.getElementById('dashboard-error').textContent = `Die Übersicht konnte nicht geladen werden: ${error.message}`;
  }
}

// ---------- Waiting queue (GET /api/waiting, sorted on the server) ----------
// Two different measurements – always labeled as what they are. Neither one says
// anything about what the employer is doing.

const WAITING_LIMIT = 6;
let waitingSort = 'SinceSubmission';

const WAITING_SUBTITLE = {
  SinceSubmission: 'Tage seit Ihrem eingetragenen Bewerbungsdatum · offene Bewerbungen',
  SinceStatusChange: 'Tage seit dem zuletzt in BewerbungsPilot erfassten Statuswechsel',
};

const days = (n) => (n === 1 ? '1 Tag' : `${n} Tagen`);

async function loadWaiting() {
  const list = document.getElementById('waiting-list');
  document.getElementById('waiting-subtitle').textContent = WAITING_SUBTITLE[waitingSort];
  document.querySelectorAll('[data-waiting-sort]').forEach((b) =>
    b.setAttribute('aria-pressed', String(b.dataset.waitingSort === waitingSort)));

  let items;
  try {
    items = await api.getWaiting(waitingSort);
  } catch (error) {
    list.innerHTML = `<li class="activity-empty">Wartende Bewerbungen konnten nicht geladen werden: ${escapeHtml(error.message)}</li>`;
    return;
  }

  document.getElementById('waiting-more').hidden = items.length <= WAITING_LIMIT;
  if (items.length === 0) {
    list.innerHTML = `<li class="activity-empty">Keine offenen Bewerbungen. Hier erscheinen versendete Bewerbungen
      (Beworben, In Prüfung, Vorstellungsgespräch), solange kein Angebot und keine Absage eingetragen ist.</li>`;
    return;
  }

  list.innerHTML = items.slice(0, WAITING_LIMIT).map((item) => {
    const primary = waitingSort === 'SinceSubmission' ? item.daysSinceSubmission : item.daysSinceStatusChange;
    const missing = waitingSort === 'SinceSubmission' ? 'Bewerbungsdatum fehlt' : 'Kein Statuswechsel erfasst';
    const submission = item.daysSinceSubmission === null
      ? 'Bewerbungsdatum fehlt'
      : `Bewerbung vor ${days(item.daysSinceSubmission)} (${formatDate(item.appliedOn)})`;
    const change = item.daysSinceStatusChange === null
      ? 'kein Statuswechsel erfasst'
      : `Status zuletzt erfasst vor ${days(item.daysSinceStatusChange)}`;
    const label = primary === null ? missing : `${primary} ${primary === 1 ? 'Tag' : 'Tage'}`;

    return `
      <li>
        <a class="activity-item waiting-item" href="/bewerbungen.html?id=${item.id}" aria-label="${escapeHtml(item.companyName)}: ${label}">
          <span class="waiting-days${primary === null ? ' is-missing' : ''}" aria-hidden="true">
            <strong>${primary ?? '–'}</strong><span>${primary === null ? 'k. A.' : primary === 1 ? 'Tag' : 'Tage'}</span>
          </span>
          <span class="activity-main">
            <span class="activity-text">${escapeHtml(item.companyName)}</span>
            <span class="activity-meta">${escapeHtml(item.position)}</span>
            ${primary === null ? `<span class="activity-meta waiting-missing">${missing}</span>` : ''}
            <span class="activity-meta">${waitingSort === 'SinceSubmission' ? change : submission}</span>
          </span>
          ${statusBadge(item.status)}
        </a>
      </li>`;
  }).join('');
}

document.querySelectorAll('[data-waiting-sort]').forEach((button) =>
  button.addEventListener('click', () => {
    waitingSort = button.dataset.waitingSort;
    loadWaiting();
  }));

// ---------- KPI cards ----------

function renderKpis(data) {
  const cards = [
    { label: 'Bewerbungen insgesamt', value: data.total, hint: data.drafts === 1 ? 'davon 1 Entwurf' : `davon ${data.drafts} Entwürfe` },
    { label: 'In Bearbeitung', value: data.inProgress, hint: 'Beworben oder in Prüfung' },
    { label: 'Vorstellungsgespräche', value: data.interviews, hint: 'Aktuell in dieser Phase', accent: 'Vorstellungsgespraech' },
    { label: 'Angebote', value: data.offers, hint: 'Erhaltene Zusagen', accent: 'Angebot' },
    { label: 'Absagen', value: data.rejections, hint: 'Abgeschlossen', accent: 'Absage' },
  ];

  document.getElementById('kpis').innerHTML = cards.map((c) => `
    <article class="card kpi">
      <h2 class="kpi-label">${c.accent ? `<span class="kpi-dot" style="background:${STATUS_COLORS[c.accent]}"></span>` : ''}${c.label}</h2>
      <p class="kpi-value">${c.value}</p>
      <p class="kpi-hint">${c.hint}</p>
    </article>`).join('');
}

// ---------- Charts ----------

function baseOptions() {
  return {
    responsive: true,
    maintainAspectRatio: false,
    animation: reducedMotion ? false : { duration: 600, easing: 'easeOutQuart' },
    plugins: {
      legend: { display: false },
      tooltip: {
        backgroundColor: '#111827',
        padding: 10,
        cornerRadius: 6,
        titleFont: { weight: '600' },
        displayColors: false,
      },
    },
  };
}

function showChartEmpty(boxId, message) {
  const box = document.getElementById(boxId);
  box.querySelector('canvas')?.setAttribute('hidden', '');
  box.querySelector('.chart-empty')?.remove();
  box.insertAdjacentHTML('afterbegin', `<p class="chart-empty">${message}</p>`);
}

function clearChartEmpty(boxId) {
  const box = document.getElementById(boxId);
  box.querySelector('.chart-empty')?.remove();
  box.querySelector('canvas')?.removeAttribute('hidden');
}

function renderActivityChart(months) {
  charts.activity?.destroy();
  if (months.every((m) => m.count === 0)) {
    showChartEmpty('activity-box', 'In den letzten sechs Monaten wurden noch keine Bewerbungen versendet.');
    return;
  }
  clearChartEmpty('activity-box');

  const short = new Intl.DateTimeFormat('de-DE', { month: 'short' });
  const long = new Intl.DateTimeFormat('de-DE', { month: 'long', year: 'numeric' });
  const dates = months.map((m) => new Date(m.year, m.month - 1, 1));

  const options = baseOptions();
  options.plugins.tooltip.callbacks = {
    title: (items) => long.format(dates[items[0].dataIndex]),
    label: (item) => (item.raw === 1 ? '1 Bewerbung' : `${item.raw} Bewerbungen`),
  };
  options.scales = {
    x: { grid: { display: false }, border: { display: false }, ticks: { color: '#64748B' } },
    y: {
      beginAtZero: true,
      border: { display: false },
      grid: { color: '#EEF2F7' },
      ticks: { color: '#64748B', precision: 0 },
    },
  };

  charts.activity = new Chart(document.getElementById('activity-chart'), {
    type: 'bar',
    data: {
      labels: dates.map((d) => short.format(d)),
      datasets: [{
        data: months.map((m) => m.count),
        backgroundColor: '#2563EB',
        hoverBackgroundColor: '#1D4ED8',
        borderRadius: 4,
        maxBarThickness: 36,
      }],
    },
    options,
  });
}

function renderStatusChart(data) {
  charts.status?.destroy();
  const rate = data.responseRatePercent;
  document.getElementById('response-rate').textContent = rate === null
    ? 'Noch keine versendeten Bewerbungen'
    : `Rückmeldungsquote: ${rate} % der versendeten Bewerbungen`;

  const legend = document.getElementById('status-legend');
  legend.innerHTML = data.statusDistribution.map((s) => `
    <li>
      <span class="legend-swatch" style="background:${STATUS_COLORS[s.status]}"></span>
      <span class="legend-label">${escapeHtml(statusLabel(s.status))}</span>
      <span class="legend-value">${s.count}${s.percent === null ? '' : ` <span class="muted">(${s.percent} %)</span>`}</span>
    </li>`).join('');

  if (data.total === 0) {
    showChartEmpty('status-box', 'Noch keine Bewerbungen vorhanden.');
    return;
  }
  clearChartEmpty('status-box');

  const options = baseOptions();
  options.cutout = '68%';
  options.plugins.tooltip.callbacks = {
    label: (item) => {
      const s = data.statusDistribution[item.dataIndex];
      return `${statusLabel(s.status)}: ${s.count} (${s.percent} %)`;
    },
  };

  charts.status = new Chart(document.getElementById('status-chart'), {
    type: 'doughnut',
    data: {
      labels: data.statusDistribution.map((s) => statusLabel(s.status)),
      datasets: [{
        data: data.statusDistribution.map((s) => s.count),
        backgroundColor: data.statusDistribution.map((s) => STATUS_COLORS[s.status]),
        borderColor: '#FFFFFF',
        borderWidth: 2,
        hoverOffset: 4,
      }],
    },
    options,
  });
}

// ---------- Upcoming events (GET /api/calendar/upcoming, already in date order) ----------

const weekdayShort = new Intl.DateTimeFormat('de-DE', { weekday: 'short' });

function renderUpcoming(events) {
  const list = document.getElementById('upcoming-events');
  if (events.length === 0) {
    list.innerHTML = `<li class="activity-empty">Keine anstehenden Termine. Sobald Sie ein Gespräch planen oder eine
      Bewerbungsfrist für einen Entwurf festlegen, erscheint es hier.</li>`;
    return;
  }

  list.innerHTML = events.map((e) => {
    const isInterview = e.kind === 'Interview';
    const title = isInterview ? `Vorstellungsgespräch · ${labelOf(INTERVIEW_TYPES, e.interviewType)}` : 'Bewerbungsfrist';
    const day = new Date(`${e.date}T00:00:00`);
    const when = `${weekdayShort.format(day)}, ${formatDate(e.date)}${isInterview ? `, ${formatTime(e.time)}` : ' (ganztägig)'}`;
    return `
      <li>
        <a class="activity-item upcoming-item" href="/bewerbungen.html?id=${e.applicationId}&tab=schedule">
          ${dateBlock(e.date, `date-block-sm kind-${e.kind}`)}
          <span class="activity-main">
            <span class="activity-text">${title}</span>
            <span class="activity-meta">${escapeHtml(e.companyName)} · ${escapeHtml(e.position)}</span>
            <span class="activity-meta">${when}</span>
          </span>
          ${statePill(e.kind, e.state)}
        </a>
      </li>`;
  }).join('');
}

// ---------- Recent activity ----------

function renderRecentActivity(items) {
  const list = document.getElementById('recent-activity');
  if (items.length === 0) {
    list.innerHTML = '<li class="activity-empty">Noch keine Aktivitäten. Legen Sie Ihre erste Bewerbung an.</li>';
    return;
  }

  list.innerHTML = items.map((item) => `
    <li>
      <a class="activity-item" href="/bewerbungen.html?id=${item.applicationId}">
        <span class="timeline-dot activity-${item.type}" aria-hidden="true"></span>
        <span class="activity-main">
          <span class="activity-text">${escapeHtml(item.description)}</span>
          <span class="activity-meta">${escapeHtml(item.companyName)} · ${escapeHtml(item.position)}</span>
        </span>
        <time class="activity-time" datetime="${item.occurredAt}">${formatDateTime(item.occurredAt)}</time>
      </a>
    </li>`).join('');
}

// Chart.js is loaded with "defer", which runs before module scripts – so window.Chart exists here.
Chart.defaults.font.family = getComputedStyle(document.body).fontFamily;
Chart.defaults.font.size = 12;
loadDashboard();
