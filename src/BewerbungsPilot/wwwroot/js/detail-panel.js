// Slide-in detail panel for one application (opens from the right).
// Uses a native <dialog>, which gives us focus trapping, the Escape key
// and returning focus to the opening button for free.

import { api } from './api.js';
import { statusBadge, statusLabel, formatDate, formatDateTime, escapeHtml, showToast, confirmDialog } from './ui.js';
import { openApplicationForm } from './application-form.js';

let panel;
let current = null; // { application, events } currently shown
let callbacks = {};

const CLOSE_ICON = `<svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"
  stroke-linecap="round" aria-hidden="true"><path d="M18 6 6 18M6 6l12 12"/></svg>`;

function buildPanel() {
  panel = document.createElement('dialog');
  panel.className = 'side-panel';
  panel.setAttribute('aria-labelledby', 'panel-title');
  document.body.append(panel);

  // Clicking the dark overlay (outside the panel) closes it.
  panel.addEventListener('click', (event) => {
    const box = panel.getBoundingClientRect();
    const outside = event.clientX < box.left || event.clientX > box.right
      || event.clientY < box.top || event.clientY > box.bottom;
    if (event.target === panel && outside) panel.close();
  });
}

// Opens the panel for an application.
// callbacks.onChanged(application) – after editing; callbacks.onDeleted(id) – after deleting.
export async function openDetailPanel(id, { onChanged, onDeleted } = {}) {
  if (!panel) buildPanel();
  callbacks = { onChanged, onDeleted };

  try {
    current = await api.getApplication(id);
  } catch (error) {
    showToast(error.message, 'error');
    if (error.status === 404) onDeleted?.(id); // already gone → refresh the list
    return;
  }

  render('overview');
  if (!panel.open) panel.showModal();
  panel.querySelector('[data-close]').focus();
}

function render(activeTab) {
  const { application: a, events } = current;

  panel.innerHTML = `
    <header class="panel-header">
      <div class="panel-heading">
        <div>${statusBadge(a.status)}</div>
        <h2 class="panel-title" id="panel-title">${escapeHtml(a.companyName)}</h2>
        <p class="panel-subtitle">${escapeHtml(a.position)}</p>
      </div>
      <button type="button" class="icon-btn" data-close aria-label="Detailansicht schließen">${CLOSE_ICON}</button>
    </header>

    <div class="panel-actions">
      <button type="button" class="btn btn-secondary" data-edit>
        <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"
          stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M12 20h9M16.5 3.5a2.1 2.1 0 0 1 3 3L7 19l-4 1 1-4Z"/></svg>
        Bearbeiten
      </button>
      <button type="button" class="btn btn-danger-ghost" data-delete>
        <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"
          stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M3 6h18M8 6V4h8v2M19 6l-1 14H6L5 6"/></svg>
        Löschen
      </button>
    </div>

    <div class="tabs" role="tablist" aria-label="Bereiche">
      ${tabButton('overview', 'Überblick', activeTab)}
      ${tabButton('history', `Verlauf <span class="tab-count">${events.length}</span>`, activeTab)}
    </div>

    <div class="panel-body">
      <section class="tab-panel" role="tabpanel" id="tab-overview" aria-labelledby="tab-btn-overview"${activeTab === 'overview' ? '' : ' hidden'}>
        ${overviewHtml(a)}
      </section>
      <section class="tab-panel" role="tabpanel" id="tab-history" aria-labelledby="tab-btn-history"${activeTab === 'history' ? '' : ' hidden'}>
        ${historyHtml(events)}
      </section>
    </div>`;

  panel.querySelector('[data-close]').addEventListener('click', () => panel.close());
  panel.querySelector('[data-edit]').addEventListener('click', editCurrent);
  panel.querySelector('[data-delete]').addEventListener('click', deleteCurrent);
  setUpTabs();
}

function tabButton(key, label, activeTab) {
  const selected = key === activeTab;
  return `<button type="button" class="tab" role="tab" id="tab-btn-${key}" aria-controls="tab-${key}"
    aria-selected="${selected}" tabindex="${selected ? 0 : -1}" data-tab="${key}">${label}</button>`;
}

// Tabs: click to switch; arrow keys move between tabs (standard keyboard pattern).
function setUpTabs() {
  const tabs = [...panel.querySelectorAll('[role="tab"]')];

  const select = (tab) => {
    tabs.forEach((t) => {
      const isSelected = t === tab;
      t.setAttribute('aria-selected', String(isSelected));
      t.tabIndex = isSelected ? 0 : -1;
      panel.querySelector(`#${t.getAttribute('aria-controls')}`).hidden = !isSelected;
    });
    tab.focus();
  };

  tabs.forEach((tab, index) => {
    tab.addEventListener('click', () => select(tab));
    tab.addEventListener('keydown', (event) => {
      if (event.key === 'ArrowRight') select(tabs[(index + 1) % tabs.length]);
      if (event.key === 'ArrowLeft') select(tabs[(index - 1 + tabs.length) % tabs.length]);
    });
  });
}

function overviewHtml(a) {
  const link = a.jobUrl
    ? `<a href="${escapeHtml(a.jobUrl)}" target="_blank" rel="noopener noreferrer" class="external-link">Stellenanzeige öffnen
        <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"
          stroke-linejoin="round" aria-hidden="true"><path d="M7 17 17 7M8 7h9v9"/></svg>
        <span class="visually-hidden">(öffnet in neuem Tab)</span></a>`
    : '<span class="muted">Nicht hinterlegt</span>';

  const item = (label, value) => `<div class="detail-item"><dt>${label}</dt><dd>${value}</dd></div>`;

  return `
    <dl class="detail-grid">
      ${item('Status', escapeHtml(statusLabel(a.status)))}
      ${item('Ort', escapeHtml(a.location ?? '–'))}
      ${item('Beworben am', formatDate(a.appliedOn))}
      ${item('Bewerbungsfrist', formatDate(a.deadline))}
      ${item('Stellenanzeige', link)}
    </dl>

    <h3 class="section-title">Notizen</h3>
    ${a.notes
      ? `<p class="notes">${escapeHtml(a.notes)}</p>`
      : '<p class="muted">Keine Notizen vorhanden.</p>'}

    <p class="meta">Angelegt am ${formatDateTime(a.createdAt)} · Zuletzt geändert am ${formatDateTime(a.updatedAt)}</p>`;
}

function historyHtml(events) {
  if (events.length === 0) return '<p class="muted">Noch keine Einträge.</p>';

  const items = events.map((e) => `
    <li class="timeline-item timeline-${e.type}">
      <span class="timeline-dot" aria-hidden="true"></span>
      <div>
        <div class="timeline-text">${escapeHtml(e.description)}</div>
        <time class="timeline-time" datetime="${e.occurredAt}">${formatDateTime(e.occurredAt)}</time>
      </div>
    </li>`).join('');

  return `<ol class="timeline">${items}</ol>`;
}

function editCurrent() {
  openApplicationForm({
    application: current.application,
    onSaved: async (saved) => {
      showToast('Änderungen gespeichert');
      current = await api.getApplication(saved.id);
      const activeTab = panel.querySelector('[aria-selected="true"]')?.dataset.tab ?? 'overview';
      render(activeTab);
      callbacks.onChanged?.(saved);
    },
  });
}

async function deleteCurrent() {
  const a = current.application;
  const confirmed = await confirmDialog({
    title: 'Bewerbung löschen?',
    message: `Die Bewerbung bei „${a.companyName}" (${a.position}) wird mit ihrem gesamten Verlauf dauerhaft gelöscht. Dieser Schritt kann nicht rückgängig gemacht werden.`,
    confirmLabel: 'Endgültig löschen',
    danger: true,
  });
  if (!confirmed) return;

  try {
    await api.deleteApplication(a.id);
    showToast('Bewerbung gelöscht');
  } catch (error) {
    showToast(error.message, 'error');
    if (error.status !== 404) return; // 404: it is gone anyway → still close and refresh
  }
  panel.close();
  callbacks.onDeleted?.(a.id);
}
