// "Dokumente" tab of the detail panel: upload (drag-and-drop or file picker),
// list, PDF preview, download and delete.
// The real validation happens on the server (DocumentService.ValidateUpload);
// the checks here only give faster feedback.

import { api, ApiError } from './api.js';
import { escapeHtml, formatDateTime, showToast, confirmDialog, setButtonSaving, setButtonSaved, resetButton, wait } from './ui.js';

export const CATEGORIES = [
  { value: 'Lebenslauf', label: 'Lebenslauf' },
  { value: 'Anschreiben', label: 'Anschreiben' },
  { value: 'Zeugnis', label: 'Zeugnis' },
  { value: 'Stellenanzeige', label: 'Stellenanzeige' },
  { value: 'Sonstiges', label: 'Sonstiges' },
];
const MAX_BYTES = 10 * 1024 * 1024;

const ICON = {
  upload: '<path d="M12 16V4M7 9l5-5 5 5M4 20h16"/>',
  eye: '<path d="M2 12s3.5-7 10-7 10 7 10 7-3.5 7-10 7S2 12 2 12Z"/><circle cx="12" cy="12" r="3"/>',
  download: '<path d="M12 4v12M7 11l5 5 5-5M4 20h16"/>',
  trash: '<path d="M3 6h18M8 6V4h8v2M19 6l-1 14H6L5 6"/>',
  close: '<path d="M18 6 6 18M6 6l12 12"/>',
};
const icon = (name, size = 16) => `<svg width="${size}" height="${size}" viewBox="0 0 24 24" fill="none" stroke="currentColor"
  stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">${ICON[name]}</svg>`;

// Dropping a file outside the upload area must not make the browser open the PDF instead of the app.
for (const type of ['dragover', 'drop']) {
  document.addEventListener(type, (event) => {
    if (event.dataTransfer?.types.includes('Files')) event.preventDefault();
  });
}

export function formatFileSize(bytes) {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${Math.round(bytes / 1024)} KB`;
  return `${(bytes / (1024 * 1024)).toLocaleString('de-DE', { maximumFractionDigits: 1 })} MB`;
}

const categoryLabel = (value) => CATEGORIES.find((c) => c.value === value)?.label ?? value;

// ---------- HTML ----------

export function documentsHtml(documents, highlightId) {
  const options = CATEGORIES.map((c) => `<option value="${c.value}">${c.label}</option>`).join('');

  const list = documents.length === 0
    ? '<p class="muted documents-empty">Noch keine Dokumente. Hinterlegen Sie hier genau die Unterlagen, die Sie versendet haben.</p>'
    : `<ul class="document-list">${documents.map((d) => documentRowHtml(d, d.id === highlightId)).join('')}</ul>`;

  return `
    <div class="upload">
      <div class="dropzone" data-dropzone>
        <span class="dropzone-icon">${icon('upload', 20)}</span>
        <p class="dropzone-text"><strong>PDF hierher ziehen</strong> oder</p>
        <label class="btn btn-secondary btn-sm file-button">
          Datei auswählen
          <input type="file" accept="application/pdf,.pdf" class="visually-hidden" data-file-input>
        </label>
        <p class="dropzone-hint">Nur PDF-Dateien, höchstens 10 MB</p>
      </div>

      <div class="upload-staged" data-staged hidden>
        <div class="staged-file">
          <span class="doc-icon" aria-hidden="true">PDF</span>
          <div class="doc-main">
            <div class="doc-title" data-staged-name></div>
            <div class="doc-meta" data-staged-size></div>
          </div>
          <button type="button" class="icon-btn" data-unstage aria-label="Auswahl entfernen">${icon('close')}</button>
        </div>
        <div class="upload-row">
          <div class="field" data-field="category">
            <label for="doc-category">Kategorie<span class="required" aria-hidden="true">*</span></label>
            <select class="select" id="doc-category" aria-required="true" aria-describedby="doc-category-error">
              <option value="">Bitte wählen …</option>${options}
            </select>
            <div class="field-error" id="doc-category-error"></div>
          </div>
          <button type="button" class="btn btn-primary" data-upload-submit>Hochladen</button>
        </div>
      </div>

      <div class="field-error" role="alert" data-file-error></div>
    </div>

    <h3 class="section-title documents-heading">Hinterlegte Dokumente</h3>
    ${list}`;
}

function documentRowHtml(d, highlight) {
  const name = escapeHtml(d.originalFileName);
  return `
    <li class="document-row${highlight ? ' row-new' : ''}" data-document-id="${d.id}">
      <span class="doc-icon" aria-hidden="true">PDF</span>
      <div class="doc-main">
        <button type="button" class="doc-title doc-title-button" data-preview="${d.id}">${name}</button>
        <div class="doc-meta">
          <span class="doc-category">${escapeHtml(categoryLabel(d.category))}</span>
          <span>${formatFileSize(d.sizeBytes)}</span>
          <span>${formatDateTime(d.uploadedAt)}</span>
        </div>
        <div class="doc-hash" title="SHA-256: ${d.sha256}">Prüfsumme ${d.sha256.slice(0, 12)}…</div>
      </div>
      <div class="doc-actions">
        <button type="button" class="icon-btn" data-preview="${d.id}" aria-label="Vorschau: ${name}">${icon('eye')}</button>
        <a class="icon-btn" href="${api.downloadUrl(d.id)}" download aria-label="Herunterladen: ${name}">${icon('download')}</a>
        <button type="button" class="icon-btn icon-btn-danger" data-delete-document="${d.id}" aria-label="Löschen: ${name}">${icon('trash')}</button>
      </div>
    </li>`;
}

// ---------- Behavior ----------

// container: the tab element; application: { id, companyName }; documents: list;
// onChanged(highlightId): called after an upload or delete so the panel can reload.
export function setUpDocumentsTab(container, application, documents, onChanged) {
  const dropzone = container.querySelector('[data-dropzone]');
  const fileInput = container.querySelector('[data-file-input]');
  const staged = container.querySelector('[data-staged]');
  const fileError = container.querySelector('[data-file-error]');
  const categorySelect = container.querySelector('#doc-category');
  const categoryField = container.querySelector('[data-field="category"]');
  const submit = container.querySelector('[data-upload-submit]');
  let selectedFile = null;

  function clearErrors() {
    fileError.textContent = '';
    categoryField.classList.remove('has-error');
    categoryField.querySelector('.field-error').textContent = '';
  }

  // Shows the chosen file and asks for a category before uploading.
  function stage(file) {
    clearErrors();
    const isPdf = file.name.toLowerCase().endsWith('.pdf');
    if (!isPdf) {
      fileError.textContent = 'Es werden nur PDF-Dateien unterstützt.';
      return;
    }
    if (file.size > MAX_BYTES) {
      fileError.textContent = 'Die Datei ist zu groß. Erlaubt sind höchstens 10 MB.';
      return;
    }
    selectedFile = file;
    container.querySelector('[data-staged-name]').textContent = file.name;
    container.querySelector('[data-staged-size]').textContent = formatFileSize(file.size);
    dropzone.hidden = true;
    staged.hidden = false;
    categorySelect.focus();
  }

  function unstage() {
    selectedFile = null;
    fileInput.value = '';
    staged.hidden = true;
    dropzone.hidden = false;
    clearErrors();
  }

  fileInput.addEventListener('change', () => fileInput.files[0] && stage(fileInput.files[0]));
  container.querySelector('[data-unstage]').addEventListener('click', () => {
    unstage();
    fileInput.focus();
  });

  // Drag-and-drop: blue highlight while a file is above the area.
  let dragDepth = 0; // counts enter/leave of child elements
  dropzone.addEventListener('dragenter', (event) => {
    event.preventDefault();
    dragDepth++;
    dropzone.classList.add('is-dragover');
  });
  dropzone.addEventListener('dragleave', () => {
    if (--dragDepth === 0) dropzone.classList.remove('is-dragover');
  });
  dropzone.addEventListener('dragover', (event) => event.preventDefault());
  dropzone.addEventListener('drop', (event) => {
    event.preventDefault();
    dragDepth = 0;
    dropzone.classList.remove('is-dragover');
    const files = event.dataTransfer.files;
    if (files.length > 1) fileError.textContent = 'Bitte jeweils nur eine Datei hochladen.';
    else if (files.length === 1) stage(files[0]);
  });

  submit.addEventListener('click', async () => {
    clearErrors();
    if (!categorySelect.value) {
      categoryField.classList.add('has-error');
      categoryField.querySelector('.field-error').textContent = 'Bitte wählen Sie eine Kategorie aus.';
      categorySelect.focus();
      return;
    }

    setButtonSaving(submit);
    submit.textContent = 'Wird hochgeladen …';
    try {
      const uploaded = await api.uploadDocument(application.id, selectedFile, categorySelect.value);
      setButtonSaved(submit);
      submit.textContent = 'Hochgeladen ✓';
      showToast(`„${uploaded.originalFileName}" hochgeladen`);
      await wait(500);
      onChanged(uploaded.id);
    } catch (error) {
      resetButton(submit);
      if (!(error instanceof ApiError)) throw error;
      fileError.textContent = error.errors.file?.join(' ') ?? error.errors.category?.join(' ') ?? error.message;
    }
  });

  // Preview / delete buttons in the list (event delegation).
  container.addEventListener('click', async (event) => {
    const previewButton = event.target.closest('[data-preview]');
    if (previewButton) {
      openPreview(documents.find((d) => d.id === Number(previewButton.dataset.preview)));
      return;
    }

    const deleteButton = event.target.closest('[data-delete-document]');
    if (deleteButton) {
      const doc = documents.find((d) => d.id === Number(deleteButton.dataset.deleteDocument));
      const confirmed = await confirmDialog({
        title: 'Dokument löschen?',
        message: `„${doc.originalFileName}" (${categoryLabel(doc.category)}) wird dauerhaft gelöscht – die Datei und ihre Angaben. Dieser Schritt kann nicht rückgängig gemacht werden.`,
        confirmLabel: 'Endgültig löschen',
        danger: true,
      });
      if (!confirmed) return;
      try {
        await api.deleteDocument(doc.id);
        showToast('Dokument gelöscht');
      } catch (error) {
        showToast(error.message, 'error');
      }
      onChanged(null);
    }
  });
}

// ---------- PDF preview ----------

let previewDialog;

function buildPreviewDialog() {
  previewDialog = document.createElement('dialog');
  previewDialog.className = 'dialog preview-dialog';
  previewDialog.setAttribute('aria-labelledby', 'preview-title');
  previewDialog.innerHTML = `
    <div class="preview-header">
      <div class="preview-heading">
        <span class="doc-category" data-preview-category></span>
        <h2 class="dialog-title" id="preview-title" data-preview-name></h2>
      </div>
      <a class="btn btn-secondary btn-sm" data-preview-download download>${icon('download', 15)} Herunterladen</a>
      <button type="button" class="icon-btn" data-preview-close aria-label="Vorschau schließen">${icon('close', 18)}</button>
    </div>
    <div class="preview-body">
      <p class="preview-status" data-preview-status role="status"></p>
      <iframe class="preview-frame" data-preview-frame title="PDF-Vorschau" hidden></iframe>
    </div>`;
  document.body.append(previewDialog);

  previewDialog.querySelector('[data-preview-close]').addEventListener('click', () => previewDialog.close());
  previewDialog.addEventListener('close', () => {
    const frame = previewDialog.querySelector('[data-preview-frame]');
    frame.onload = null;
    frame.src = 'about:blank'; // stop showing the document
  });
}

async function openPreview(doc) {
  if (!previewDialog) buildPreviewDialog();
  const frame = previewDialog.querySelector('[data-preview-frame]');
  const status = previewDialog.querySelector('[data-preview-status]');

  previewDialog.querySelector('[data-preview-name]').textContent = doc.originalFileName;
  previewDialog.querySelector('[data-preview-category]').textContent = categoryLabel(doc.category);
  previewDialog.querySelector('[data-preview-download]').href = api.downloadUrl(doc.id);
  frame.hidden = true;
  status.hidden = false;
  status.classList.remove('is-error');
  status.textContent = 'Vorschau wird geladen …';
  previewDialog.showModal();

  // Check first that the file is really available – an <iframe> cannot report HTTP errors.
  let response;
  try {
    response = await fetch(api.previewUrl(doc.id), { method: 'HEAD' });
  } catch {
    response = null;
  }
  if (!response?.ok) {
    status.classList.add('is-error');
    status.textContent = 'Die Vorschau konnte nicht geladen werden. Die Datei ist möglicherweise nicht mehr vorhanden.';
    return;
  }

  frame.onload = () => {
    status.hidden = true;
    frame.hidden = false;
  };
  frame.src = api.previewUrl(doc.id);
}
