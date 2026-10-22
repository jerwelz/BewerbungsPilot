// Page script for pipeline.html: the Kanban board.
//
// Drag-and-drop uses SortableJS (loaded in pipeline.html). Every status change –
// by dragging or with the dropdown on a card – goes through saveStatus(), which
// calls PATCH /api/applications/{id}/status. The business rules (history entry,
// application date) live in ApplicationService.ChangeStatusAsync on the server.

import { api } from './api.js';
import { renderSidebar, STATUSES, statusLabel, formatDate, escapeHtml, showToast, matchesSearch } from './ui.js';
import { openApplicationForm } from './application-form.js';
import { openDetailPanel } from './detail-panel.js';

renderSidebar('pipeline');

const board = document.getElementById('board');
const switcher = document.getElementById('column-switcher');
const searchInput = document.getElementById('pipeline-search');
const reducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
const mobile = window.matchMedia('(max-width: 860px)');

const EMPTY_TEXT = {
  Entwurf: 'Keine Entwürfe. Neue Bewerbungen beginnen hier.',
  Beworben: 'Keine versendeten Bewerbungen ohne Rückmeldung.',
  InPruefung: 'Keine Bewerbung in Prüfung.',
  Vorstellungsgespraech: 'Noch keine Einladungen zum Gespräch.',
  Angebot: 'Noch keine Angebote.',
  Absage: 'Keine Absagen.',
};

let applications = [];   // the last state confirmed by the server
let sortables = [];      // one SortableJS instance per column
let activeColumn = null; // mobile: the column currently shown
let lastDragEnd = 0;     // used to ignore the click that can follow a drop

// ---------- Loading & rendering ----------

async function loadBoard() {
  try {
    applications = await api.getApplications();
    render();
  } catch (error) {
    document.getElementById('pipeline-error').textContent = `Die Pipeline konnte nicht geladen werden: ${error.message}`;
  }
}

// Builds the whole board from `applications`. Also used to undo a failed move:
// the data was not changed, so rendering again puts the card back where it was.
function render() {
  sortables.forEach((s) => s.destroy());
  sortables = [];

  board.innerHTML = STATUSES.map((s) => columnHtml(s)).join('');

  for (const status of STATUSES) {
    const list = board.querySelector(`.column-list[data-status="${status.value}"]`);
    // Newest status change first
    applications
      .filter((a) => a.status === status.value)
      .sort((a, b) => b.statusChangedAt.localeCompare(a.statusChangedAt))
      .forEach((a) => list.append(createCard(a)));
    sortables.push(createSortable(list));
  }

  activeColumn ??= STATUSES.find((s) => applications.some((a) => a.status === s.value))?.value ?? 'Entwurf';
  applySearch();
}

function columnHtml(status) {
  return `
    <section class="kanban-column" data-status="${status.value}" aria-labelledby="col-${status.value}">
      <header class="column-header">
        <span class="column-dot accent-${status.value}" aria-hidden="true"></span>
        <h2 class="column-title" id="col-${status.value}">${escapeHtml(status.label)}</h2>
        <span class="column-count" data-count aria-label="Anzahl">0</span>
      </header>
      <div class="column-list" data-status="${status.value}" role="list"
        aria-label="Bewerbungen mit Status ${escapeHtml(status.label)}"></div>
      <p class="column-empty" data-empty hidden></p>
    </section>`;
}

function createCard(a) {
  const options = STATUSES
    .map((s) => `<option value="${s.value}"${s.value === a.status ? ' selected' : ''}>${escapeHtml(s.label)}</option>`)
    .join('');
  const meta = [a.location && escapeHtml(a.location), a.appliedOn && `Beworben ${formatDate(a.appliedOn)}`]
    .filter(Boolean).join(' · ');

  const card = document.createElement('article');
  card.className = `kanban-card accent-${a.status}`;
  card.dataset.id = a.id;
  card.setAttribute('role', 'listitem');
  card.innerHTML = `
    <button type="button" class="card-title" aria-label="${escapeHtml(a.companyName)} – Details öffnen">${escapeHtml(a.companyName)}</button>
    <p class="card-position">${escapeHtml(a.position)}</p>
    ${meta ? `<p class="card-meta">${meta}</p>` : ''}
    <div class="card-footer">
      <label class="visually-hidden" for="status-${a.id}">Status von ${escapeHtml(a.companyName)} ändern</label>
      <select class="status-select" id="status-${a.id}" data-status-select>${options}</select>
    </div>`;
  return card;
}

// ---------- Counters, empty states, search, mobile switcher ----------

function applySearch() {
  for (const card of board.querySelectorAll('.kanban-card')) {
    const application = applications.find((a) => a.id === Number(card.dataset.id));
    card.hidden = !matchesSearch(application, searchInput.value);
  }
  updateColumns();
}

function updateColumns() {
  const searching = searchInput.value.trim() !== '';

  for (const column of board.querySelectorAll('.kanban-column')) {
    const cards = [...column.querySelectorAll('.kanban-card')];
    const visible = cards.filter((c) => !c.hidden).length;
    column.querySelector('[data-count]').textContent = searching ? `${visible}/${cards.length}` : cards.length;

    const empty = column.querySelector('[data-empty]');
    empty.hidden = visible > 0;
    empty.textContent = searching && cards.length > 0 ? 'Keine Treffer in dieser Spalte.' : EMPTY_TEXT[column.dataset.status];
    column.classList.toggle('is-active', column.dataset.status === activeColumn);
  }
  renderSwitcher();
}

function renderSwitcher() {
  switcher.innerHTML = STATUSES.map((s) => {
    const count = board.querySelectorAll(`.column-list[data-status="${s.value}"] .kanban-card:not([hidden])`).length;
    return `<button type="button" class="switcher-chip" data-column="${s.value}" aria-pressed="${s.value === activeColumn}">
      <span class="column-dot accent-${s.value}" aria-hidden="true"></span>${escapeHtml(s.label)}<span class="switcher-count">${count}</span>
    </button>`;
  }).join('');
}

switcher.addEventListener('click', (event) => {
  const chip = event.target.closest('[data-column]');
  if (!chip) return;
  activeColumn = chip.dataset.column;
  updateColumns();
  switcher.querySelector(`[data-column="${activeColumn}"]`).focus();
});

searchInput.addEventListener('input', applySearch);

// ---------- Drag-and-drop (SortableJS) ----------

function createSortable(list) {
  return new Sortable(list, {
    group: 'pipeline',          // cards can move between all columns
    sort: false,                // order inside a column is not stored
    draggable: '.kanban-card',
    filter: '.status-select',   // the dropdown never starts a drag …
    preventOnFilter: false,     // … and stays usable
    forceFallback: true,        // custom drag preview we can style (lift, shadow)
    fallbackOnBody: true,
    fallbackTolerance: 4,       // a few pixels of movement before a drag starts → clicks stay clicks
    animation: reducedMotion ? 0 : 180,
    ghostClass: 'card-placeholder',
    chosenClass: 'card-chosen',
    fallbackClass: 'card-dragging',
    disabled: mobile.matches,   // phones: one column at a time, status via dropdown
    onStart: () => board.classList.add('is-dragging'),
    onMove: (event) => highlightColumn(event.to),
    onEnd: handleDrop,
  });
}

function highlightColumn(list) {
  board.querySelectorAll('.kanban-column.is-over').forEach((c) => c.classList.remove('is-over'));
  list?.closest('.kanban-column')?.classList.add('is-over');
}

async function handleDrop(event) {
  board.classList.remove('is-dragging');
  highlightColumn(null);
  lastDragEnd = Date.now();

  const newStatus = event.to.dataset.status;
  if (event.from === event.to) return;

  const updated = await saveStatus(event.item, newStatus);
  if (updated) {
    const freshCard = createCard(updated);
    event.item.replaceWith(freshCard);
    settle(freshCard);
    updateColumns();
  }
}

mobile.addEventListener('change', () => sortables.forEach((s) => s.option('disabled', mobile.matches)));

// ---------- Dropdown on the card (keyboard & touch alternative) ----------

board.addEventListener('change', async (event) => {
  const select = event.target.closest('[data-status-select]');
  if (!select) return;

  const card = select.closest('.kanban-card');
  const updated = await saveStatus(card, select.value);
  if (!updated) return;

  // Move the card to the top of its new column only after the server confirmed it.
  const freshCard = createCard(updated);
  board.querySelector(`.column-list[data-status="${updated.status}"]`).prepend(freshCard);
  card.remove();
  settle(freshCard);
  updateColumns();

  if (mobile.matches) switcher.querySelector(`[data-column="${updated.status}"]`)?.focus();
  else freshCard.querySelector('[data-status-select]').focus();
});

// ---------- Saving: shared by drag-and-drop and dropdown ----------

// Sends the new status to the server. Returns the updated application,
// or null after restoring the board if the server rejected the change.
async function saveStatus(card, newStatus) {
  const id = Number(card.dataset.id);
  const application = applications.find((a) => a.id === id);
  const oldStatus = application.status;

  card.classList.add('is-saving');
  card.setAttribute('aria-busy', 'true');

  try {
    const updated = await api.changeStatus(id, newStatus);
    Object.assign(application, updated); // local state = confirmed server state
    showToast(`${updated.companyName}: ${statusLabel(oldStatus)} → ${statusLabel(updated.status)}`);
    return updated;
  } catch (error) {
    showToast(`Status konnte nicht gespeichert werden. ${error.message}`, 'error');
    if (error.status === 404) applications = applications.filter((a) => a.id !== id);
    render(); // data unchanged → the card returns to its original column
    return null;
  }
}

// Short "settling" animation after a confirmed move.
function settle(card) {
  if (reducedMotion) return;
  card.classList.add('card-settle');
  card.addEventListener('animationend', () => card.classList.remove('card-settle'), { once: true });
}

// ---------- Opening details ----------

board.addEventListener('click', (event) => {
  const card = event.target.closest('.kanban-card');
  if (!card || event.target.closest('.card-footer')) return; // the dropdown never opens details
  if (Date.now() - lastDragEnd < 300) return;                // the click that ends a drag
  openDetailPanel(Number(card.dataset.id), { onChanged: loadBoard, onDeleted: loadBoard });
});

document.getElementById('new-application').addEventListener('click', () => {
  openApplicationForm({
    onSaved: () => {
      showToast('Bewerbung gespeichert');
      loadBoard();
    },
  });
});

loadBoard();
