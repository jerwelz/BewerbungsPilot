// Page script for bewerbungen.html: list with search, filters and sorting.
// All applications are loaded once; filtering happens here in the browser,
// so typing in the search field does not send any requests.

import { api } from './api.js';
import { renderSidebar, statusBadge, formatDate, escapeHtml, showToast, STATUSES, todayIso, matchesSearch } from './ui.js';
import { openApplicationForm } from './application-form.js';
import { openDetailPanel } from './detail-panel.js';

renderSidebar('applications');

let allApplications = [];

const listContainer = document.getElementById('application-list');
const countLabel = document.getElementById('application-count');
const controls = {
  search: document.getElementById('filter-search'),
  status: document.getElementById('filter-status'),
  period: document.getElementById('filter-period'),
  sort: document.getElementById('filter-sort'),
};
const resetButton = document.getElementById('filter-reset');

controls.status.insertAdjacentHTML('beforeend',
  STATUSES.map((s) => `<option value="${s.value}">${escapeHtml(s.label)}</option>`).join(''));

for (const control of Object.values(controls)) {
  control.addEventListener('input', () => render());
}
resetButton.addEventListener('click', resetFilters);
document.getElementById('new-application').addEventListener('click', openForm);

// Clicks anywhere in a row open the detail panel (event delegation: one listener for all rows).
listContainer.addEventListener('click', (event) => {
  const row = event.target.closest('tr[data-id]');
  if (row) openDetails(Number(row.dataset.id));
});

function openForm() {
  openApplicationForm({
    onSaved: async (saved) => {
      showToast('Bewerbung gespeichert');
      await loadApplications(saved.id);
    },
  });
}

function openDetails(id, tab) {
  openDetailPanel(id, {
    tab,
    onChanged: (saved) => loadApplications(saved.id),
    onDeleted: () => loadApplications(),
  });
}

function resetFilters() {
  controls.search.value = '';
  controls.status.value = '';
  controls.period.value = '';
  controls.sort.value = 'newest';
  render();
  controls.search.focus();
}

async function loadApplications(highlightId) {
  try {
    allApplications = await api.getApplications();
    render(highlightId);
  } catch (error) {
    listContainer.innerHTML = `<div class="empty-state"><h2>Bewerbungen konnten nicht geladen werden</h2><p>${escapeHtml(error.message)}</p></div>`;
  }
}

// ---------- Filtering & sorting ----------

function daysAgoIso(days) {
  const date = new Date();
  date.setDate(date.getDate() - days);
  return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}-${String(date.getDate()).padStart(2, '0')}`;
}

function matchesPeriod(application, period) {
  if (!period) return true;
  if (period === 'none') return !application.appliedOn;
  // ISO dates ("2026-10-22") can be compared as plain text.
  return application.appliedOn !== null
    && application.appliedOn >= daysAgoIso(Number(period))
    && application.appliedOn <= todayIso();
}

const SORTERS = {
  newest: (a, b) => b.createdAt.localeCompare(a.createdAt),
  oldest: (a, b) => a.createdAt.localeCompare(b.createdAt),
  company: (a, b) => a.companyName.localeCompare(b.companyName, 'de', { sensitivity: 'base' }),
  // Earliest deadline first; applications without a deadline go to the end.
  deadline: (a, b) => (a.deadline ?? '9999').localeCompare(b.deadline ?? '9999'),
};

function filteredApplications() {
  return allApplications
    .filter((a) => matchesSearch(a, controls.search.value))
    .filter((a) => !controls.status.value || a.status === controls.status.value)
    .filter((a) => matchesPeriod(a, controls.period.value))
    .sort(SORTERS[controls.sort.value]);
}

function filtersActive() {
  return controls.search.value.trim() !== '' || controls.status.value !== '' || controls.period.value !== '';
}

// ---------- Rendering ----------

// Overdue and soon-due deadlines are highlighted; the state comes from the server (ScheduleRules).
function deadlineCell(a) {
  if (!a.deadline) return '–';
  const text = formatDate(a.deadline);
  if (a.deadlineState === 'Overdue') return `<span class="deadline-text deadline-Overdue">${text} · überfällig</span>`;
  if (a.deadlineState === 'DueSoon') return `<span class="deadline-text deadline-DueSoon">${text}</span>`;
  return text;
}

function render(highlightId) {
  const visible = filteredApplications();
  const total = allApplications.length;
  resetButton.hidden = !filtersActive() && controls.sort.value === 'newest';

  countLabel.textContent = visible.length === total
    ? (total === 1 ? '1 Bewerbung' : `${total} Bewerbungen`)
    : `${visible.length} von ${total} Bewerbungen`;

  if (total === 0) {
    listContainer.innerHTML = `
      <div class="empty-state">
        <h2>Noch keine Bewerbungen</h2>
        <p>Legen Sie Ihre erste Bewerbung an, um den Überblick zu behalten.</p>
        <button class="btn btn-primary" type="button" data-empty-new>Neue Bewerbung</button>
      </div>`;
    listContainer.querySelector('[data-empty-new]').addEventListener('click', openForm);
    return;
  }

  if (visible.length === 0) {
    listContainer.innerHTML = `
      <div class="empty-state">
        <h2>Keine passenden Bewerbungen</h2>
        <p>Für die aktuelle Suche und die gewählten Filter gibt es keine Treffer.</p>
        <button class="btn btn-secondary" type="button" data-empty-reset>Filter zurücksetzen</button>
      </div>`;
    listContainer.querySelector('[data-empty-reset]').addEventListener('click', resetFilters);
    return;
  }

  const rows = visible.map((a) => `
    <tr data-id="${a.id}" class="row-clickable${a.id === highlightId ? ' row-new' : ''}">
      <td>
        <button type="button" class="row-link">${escapeHtml(a.companyName)}</button>
        <div class="cell-secondary">${escapeHtml(a.position)}</div>
      </td>
      <td class="cell-muted">${escapeHtml(a.location ?? '–')}</td>
      <td>${statusBadge(a.status)}</td>
      <td class="cell-muted">${deadlineCell(a)}</td>
      <td class="cell-muted">${formatDate(a.appliedOn)}</td>
    </tr>`).join('');

  listContainer.innerHTML = `
    <div class="table-wrap">
      <table class="table">
        <caption class="visually-hidden">Bewerbungen – Zeile auswählen, um Details zu öffnen</caption>
        <thead>
          <tr>
            <th scope="col">Unternehmen / Position</th>
            <th scope="col">Ort</th>
            <th scope="col">Status</th>
            <th scope="col">Frist</th>
            <th scope="col">Beworben am</th>
          </tr>
        </thead>
        <tbody>${rows}</tbody>
      </table>
    </div>`;
}

// Start: load data; "?id=5" in the URL (e.g. from the dashboard) opens that application directly.
await loadApplications();
// "&tab=schedule" opens a specific tab (links from the dashboard and the calendar).
const query = new URLSearchParams(location.search);
const requestedId = Number(query.get('id'));
if (requestedId) openDetails(requestedId, query.get('tab') ?? undefined);
