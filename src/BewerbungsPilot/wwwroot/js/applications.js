// Page script for bewerbungen.html: loads and displays the application list.

import { api } from './api.js';
import { renderSidebar, statusBadge, formatDate, escapeHtml, showToast } from './ui.js';
import { openApplicationForm } from './application-form.js';

renderSidebar('applications');

const listContainer = document.getElementById('application-list');
const countLabel = document.getElementById('application-count');

document.getElementById('new-application').addEventListener('click', openForm);

function openForm() {
  openApplicationForm({
    onSaved: async (saved) => {
      showToast('Bewerbung gespeichert');
      await loadApplications(saved.id);
    },
  });
}

async function loadApplications(highlightId) {
  try {
    const applications = await api.getApplications();
    render(applications, highlightId);
  } catch (error) {
    listContainer.innerHTML = `<div class="empty-state"><h2>Bewerbungen konnten nicht geladen werden</h2><p>${escapeHtml(error.message)}</p></div>`;
  }
}

function render(applications, highlightId) {
  countLabel.textContent = applications.length === 1 ? '1 Bewerbung' : `${applications.length} Bewerbungen`;

  if (applications.length === 0) {
    listContainer.innerHTML = `
      <div class="empty-state">
        <h2>Noch keine Bewerbungen</h2>
        <p>Legen Sie Ihre erste Bewerbung an, um den Überblick zu behalten.</p>
        <button class="btn btn-primary" type="button" data-empty-new>Neue Bewerbung</button>
      </div>`;
    listContainer.querySelector('[data-empty-new]').addEventListener('click', openForm);
    return;
  }

  const rows = applications.map((a) => `
    <tr${a.id === highlightId ? ' class="row-new"' : ''}>
      <td>
        <div class="cell-primary">${escapeHtml(a.companyName)}</div>
        <div class="cell-secondary">${escapeHtml(a.position)}</div>
      </td>
      <td class="cell-muted">${escapeHtml(a.location ?? '–')}</td>
      <td>${statusBadge(a.status)}</td>
      <td class="cell-muted">${formatDate(a.deadline)}</td>
      <td class="cell-muted">${formatDate(a.appliedOn)}</td>
    </tr>`).join('');

  listContainer.innerHTML = `
    <div class="table-wrap">
      <table class="table">
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

loadApplications();
