// Page script for index.html (Übersicht). All numbers come from GET /api/dashboard,
// which calculates them in DashboardService.cs. This file only displays them.

import { api } from './api.js';
import { renderSidebar, statusLabel, escapeHtml, formatDateTime, showToast } from './ui.js';
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
    const data = await api.getDashboard();
    renderKpis(data);
    renderActivityChart(data.monthlyActivity);
    renderStatusChart(data);
    renderRecentActivity(data.recentActivity);
  } catch (error) {
    document.getElementById('dashboard-error').textContent = `Die Übersicht konnte nicht geladen werden: ${error.message}`;
  }
}

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
