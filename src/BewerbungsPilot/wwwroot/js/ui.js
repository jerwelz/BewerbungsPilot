// Shared UI helpers: sidebar, status labels, formatting, toasts, button feedback.

export const STATUSES = [
  { value: 'Entwurf', label: 'Entwurf' },
  { value: 'Beworben', label: 'Beworben' },
  { value: 'InPruefung', label: 'In Prüfung' },
  { value: 'Vorstellungsgespraech', label: 'Vorstellungsgespräch' },
  { value: 'Angebot', label: 'Angebot' },
  { value: 'Absage', label: 'Absage' },
];

export function statusLabel(value) {
  return STATUSES.find((s) => s.value === value)?.label ?? value;
}

export function statusBadge(value) {
  return `<span class="badge status-${value}">${escapeHtml(statusLabel(value))}</span>`;
}

// Prevents user text from being interpreted as HTML (protection against XSS).
export function escapeHtml(text) {
  return String(text ?? '')
    .replaceAll('&', '&amp;')
    .replaceAll('<', '&lt;')
    .replaceAll('>', '&gt;')
    .replaceAll('"', '&quot;')
    .replaceAll("'", '&#39;');
}

const dateFormat = new Intl.DateTimeFormat('de-DE', { day: '2-digit', month: '2-digit', year: 'numeric' });

// "2026-10-22" (DateOnly from the API) → "22.10.2026"
export function formatDate(isoDate) {
  if (!isoDate) return '–';
  const [year, month, day] = isoDate.split('-').map(Number);
  return dateFormat.format(new Date(year, month - 1, day));
}

// ---------- Sidebar ----------

const ICONS = {
  overview: '<path d="M3 3h7v9H3zM14 3h7v5h-7zM14 12h7v9h-7zM3 16h7v5H3z"/>',
  applications: '<path d="M8 6h13M8 12h13M8 18h13M3 6h.01M3 12h.01M3 18h.01"/>',
  pipeline: '<path d="M3 3h5v18H3zM10 3h5v12h-5zM17 3h4v8h-4z"/>',
  schedule: '<rect x="3" y="4" width="18" height="18" rx="2"/><path d="M16 2v4M8 2v4M3 10h18"/>',
};

const NAV_ITEMS = [
  { key: 'overview', label: 'Übersicht', href: '/index.html', ready: false },
  { key: 'applications', label: 'Bewerbungen', href: '/bewerbungen.html', ready: true },
  { key: 'pipeline', label: 'Pipeline', href: '/pipeline.html', ready: false },
  { key: 'schedule', label: 'Termine & Fristen', href: '/termine.html', ready: false },
];

function icon(name, size = 18) {
  return `<svg width="${size}" height="${size}" viewBox="0 0 24 24" fill="none" stroke="currentColor"
    stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">${ICONS[name]}</svg>`;
}

export function renderSidebar(activeKey) {
  const links = NAV_ITEMS.map((item) => {
    const current = item.key === activeKey ? ' aria-current="page"' : '';
    const disabled = item.ready ? '' : ' aria-disabled="true" tabindex="-1"';
    const soon = item.ready ? '' : '<span class="nav-soon">Bald</span>';
    return `<a class="nav-link" href="${item.href}"${current}${disabled}>${icon(item.key)}<span>${item.label}</span>${soon}</a>`;
  }).join('');

  document.getElementById('sidebar').innerHTML = `
    <a class="brand" href="/">
      <span class="brand-mark">
        <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="#fff" stroke-width="2.2"
          stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M22 2 11 13M22 2l-7 20-4-9-9-4z"/></svg>
      </span>
      <span class="brand-name">BewerbungsPilot</span>
    </a>
    <nav class="nav" aria-label="Hauptnavigation">${links}</nav>`;
}

// ---------- Toasts ----------

function toastRegion() {
  let region = document.querySelector('.toast-region');
  if (!region) {
    region = document.createElement('div');
    region.className = 'toast-region';
    region.setAttribute('role', 'status');
    region.setAttribute('aria-live', 'polite');
    document.body.append(region);
  }
  return region;
}

export function showToast(message, type = 'success') {
  const toast = document.createElement('div');
  toast.className = `toast toast-${type}`;
  const path = type === 'error' ? '<path d="M12 8v4M12 16h.01"/><circle cx="12" cy="12" r="10"/>' : '<path d="M20 6 9 17l-5-5"/>';
  toast.innerHTML = `<span class="toast-icon"><svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor"
    stroke-width="2.4" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">${path}</svg></span>
    <span>${escapeHtml(message)}</span>`;
  toastRegion().append(toast);
  requestAnimationFrame(() => toast.classList.add('is-visible'));

  setTimeout(() => {
    toast.classList.remove('is-visible');
    setTimeout(() => toast.remove(), 300);
  }, 3200);
}

// ---------- Save button feedback: "Speichern" → "Speichert …" → "Gespeichert" ----------

export function setButtonSaving(button) {
  button.dataset.label ??= button.textContent;
  button.disabled = true;
  button.textContent = 'Speichert …';
}

export function setButtonSaved(button) {
  button.textContent = 'Gespeichert ✓';
  button.classList.add('is-saved');
}

export function resetButton(button) {
  button.disabled = false;
  button.classList.remove('is-saved');
  if (button.dataset.label) button.textContent = button.dataset.label;
}

export const wait = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
