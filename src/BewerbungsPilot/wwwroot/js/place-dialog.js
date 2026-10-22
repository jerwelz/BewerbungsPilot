// Dialog to confirm a place: search via Nominatim (only on click) or enter coordinates by hand.
// Used for employer locations (with precision choice) and for the starting point.

import { api, ApiError } from './api.js';
import { escapeHtml, clearFieldErrors, showFieldErrors, setButtonSaving, resetButton } from './ui.js';

export const PRECISIONS = [
  { value: 'Exact', label: 'Genaue Arbeitsadresse' },
  { value: 'Approximate', label: 'Ungefährer Standort (z. B. Straße, Stadtteil)' },
  { value: 'City', label: 'Nur Stadt / Ort' },
  { value: 'Remote', label: 'Remote – kein Arbeitsort' },
];

let dialog;
let options = {};
let results = [];

// "53,55" or "53.55" → 53.55; empty → null; garbage → NaN (the server/validation reports it)
const parseNumber = (text) => (text.trim() === '' ? null : Number(text.trim().replace(',', '.')));

function buildDialog() {
  dialog = document.createElement('dialog');
  dialog.className = 'dialog place-dialog';
  dialog.setAttribute('aria-labelledby', 'place-title');
  dialog.innerHTML = `
    <form method="dialog" novalidate>
      <div class="dialog-header">
        <h2 class="dialog-title" id="place-title"></h2>
        <button type="button" class="icon-btn" data-close aria-label="Schließen">
          <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"
            stroke-linecap="round" aria-hidden="true"><path d="M18 6 6 18M6 6l12 12"/></svg>
        </button>
      </div>
      <div class="dialog-body">
        <div class="form-alert" role="alert" data-form-alert></div>
        <p class="dialog-text" data-intro></p>

        <div class="field" data-field="precision" data-precision-field>
          <label for="p-precision">Genauigkeit</label>
          <select class="select" id="p-precision">${PRECISIONS.map((p) => `<option value="${p.value}">${p.label}</option>`).join('')}</select>
          <div class="field-error" id="p-precision-error"></div>
          <p class="field-hint">Prüfen Sie, ob die Adresse der tatsächliche Arbeitsort ist – nicht nur der Firmensitz.</p>
        </div>

        <div data-position-fields>
          <div class="field" data-field="query">
            <label for="p-query">Ort oder Adresse suchen</label>
            <div class="place-search">
              <input class="input" id="p-query" autocomplete="off">
              <button type="button" class="btn btn-secondary" data-search>Suchen</button>
            </div>
            <div class="field-error" id="p-query-error"></div>
            <p class="field-hint">Die Suche wird erst beim Klick auf „Suchen“ an den Dienst Nominatim der OpenStreetMap
              Foundation gesendet – übertragen wird nur dieser Suchtext.</p>
          </div>

          <fieldset class="place-results" data-results hidden>
            <legend>Treffer – bitte den passenden Ort auswählen</legend>
            <div data-result-list></div>
            <p class="place-attribution" data-attribution></p>
          </fieldset>

          <div class="form-grid place-coordinates">
            <div class="field" data-field="latitude">
              <label for="p-latitude">Breitengrad</label>
              <input class="input" id="p-latitude" inputmode="decimal" placeholder="z. B. 53,5511">
              <div class="field-error" id="p-latitude-error"></div>
            </div>
            <div class="field" data-field="longitude">
              <label for="p-longitude">Längengrad</label>
              <input class="input" id="p-longitude" inputmode="decimal" placeholder="z. B. 9,9937">
              <div class="field-error" id="p-longitude-error"></div>
            </div>
          </div>
        </div>

        <div class="field" data-field="label">
          <label for="p-label">Bezeichnung</label>
          <input class="input" id="p-label" placeholder="z. B. Hamburg-Altona">
          <div class="field-error" id="p-label-error"></div>
        </div>
      </div>
      <div class="dialog-footer">
        <button type="button" class="btn btn-danger-ghost place-remove" data-remove>Entfernen</button>
        <button type="button" class="btn btn-secondary" data-close>Abbrechen</button>
        <button type="submit" class="btn btn-primary" data-submit>Übernehmen</button>
      </div>
    </form>`;
  document.body.append(dialog);

  dialog.querySelectorAll('[data-close]').forEach((b) => b.addEventListener('click', () => dialog.close()));
  dialog.querySelector('[data-search]').addEventListener('click', search);
  dialog.querySelector('#p-query').addEventListener('keydown', (event) => {
    if (event.key === 'Enter') {
      event.preventDefault(); // Enter searches instead of saving
      search();
    }
  });
  dialog.querySelector('#p-precision').addEventListener('input', updatePositionFields);
  dialog.querySelector('[data-result-list]').addEventListener('change', (event) => pickResult(Number(event.target.value)));
  dialog.querySelector('[data-remove]').addEventListener('click', async () => {
    await options.onRemove?.();
    dialog.close();
  });
  dialog.querySelector('form').addEventListener('submit', submit);
}

function updatePositionFields() {
  const remote = options.withPrecision && dialog.querySelector('#p-precision').value === 'Remote';
  dialog.querySelector('[data-position-fields]').hidden = remote;
}

async function search() {
  clearFieldErrors(dialog);
  const button = dialog.querySelector('[data-search]');
  const box = dialog.querySelector('[data-results]');
  setButtonSaving(button);
  button.textContent = 'Sucht …';
  try {
    const response = await api.geocode(dialog.querySelector('#p-query').value);
    results = response.results;
    box.hidden = false;
    dialog.querySelector('[data-attribution]').textContent = response.attribution;
    dialog.querySelector('[data-result-list]').innerHTML = results.length === 0
      ? '<p class="muted">Keine Treffer. Versuchen Sie eine andere Schreibweise oder geben Sie die Koordinaten von Hand ein.</p>'
      : results.map((r, i) => `
          <label class="place-result">
            <input type="radio" name="place-result" value="${i}">
            <span><span class="place-result-name">${escapeHtml(r.label)}</span>
              <span class="muted">${r.latitude.toFixed(4)}, ${r.longitude.toFixed(4)}</span></span>
          </label>`).join('');
  } catch (error) {
    if (!(error instanceof ApiError)) throw error;
    box.hidden = true;
    showFieldErrors(dialog, error.status === 400 ? error : { errors: { query: [error.message] }, message: error.message }, 'p-');
  } finally {
    resetButton(button);
  }
}

// Picking a result fills the coordinate fields – those fields are what gets saved.
function pickResult(index) {
  const r = results[index];
  dialog.querySelector('#p-latitude').value = r.latitude;
  dialog.querySelector('#p-longitude').value = r.longitude;
  dialog.querySelector('#p-label').value = r.label.split(',').slice(0, 3).join(',').trim();
  if (options.withPrecision) {
    dialog.querySelector('#p-precision').value = r.suggestedPrecision;
    updatePositionFields();
  }
}

async function submit(event) {
  event.preventDefault();
  clearFieldErrors(dialog);
  const submitButton = dialog.querySelector('[data-submit]');
  const value = {
    precision: options.withPrecision ? dialog.querySelector('#p-precision').value : null,
    latitude: parseNumber(dialog.querySelector('#p-latitude').value),
    longitude: parseNumber(dialog.querySelector('#p-longitude').value),
    label: dialog.querySelector('#p-label').value.trim() || null,
  };
  if (value.precision === 'Remote') {
    value.latitude = null;
    value.longitude = null;
  }

  setButtonSaving(submitButton);
  try {
    await options.onSave(value); // throws ApiError (or { errors }) on invalid input
    dialog.close();
  } catch (error) {
    if (!(error instanceof ApiError) && !error.errors) throw error;
    showFieldErrors(dialog, error, 'p-');
  } finally {
    resetButton(submitButton);
  }
}

// options: title, intro, withPrecision, initial { precision, latitude, longitude, label, query },
//          onSave(value) – may throw ApiError for validation, onRemove() – optional
export function openPlaceDialog(opts) {
  if (!dialog) buildDialog();
  options = opts;
  results = [];
  const initial = opts.initial ?? {};

  dialog.querySelector('#place-title').textContent = opts.title;
  dialog.querySelector('[data-intro]').textContent = opts.intro;
  dialog.querySelector('[data-precision-field]').hidden = !opts.withPrecision;
  dialog.querySelector('#p-precision').value = initial.precision && initial.precision !== 'Unknown' ? initial.precision : 'City';
  dialog.querySelector('#p-query').value = initial.query ?? '';
  dialog.querySelector('#p-latitude').value = initial.latitude ?? '';
  dialog.querySelector('#p-longitude').value = initial.longitude ?? '';
  dialog.querySelector('#p-label').value = initial.label ?? '';
  dialog.querySelector('[data-results]').hidden = true;
  dialog.querySelector('[data-remove]').hidden = !opts.onRemove;
  const submitButton = dialog.querySelector('[data-submit]');
  submitButton.dataset.label = 'Übernehmen';
  resetButton(submitButton);
  clearFieldErrors(dialog);
  updatePositionFields();

  dialog.showModal();
  dialog.querySelector(opts.withPrecision ? '#p-precision' : '#p-query').focus();
}
