// All communication with the backend happens in this file.
// Every function sends one HTTP request and returns the parsed JSON.

export class ApiError extends Error {
  constructor(status, data) {
    super(data?.title ?? `Der Server hat mit Fehler ${status} geantwortet.`);
    this.status = status;
    this.errors = data?.errors ?? {}; // field name → list of messages
  }
}

async function request(method, url, body) {
  const options = { method, headers: {} };
  if (body !== undefined) {
    options.headers['Content-Type'] = 'application/json';
    options.body = JSON.stringify(body);
  }

  let response;
  try {
    response = await fetch(url, options);
  } catch {
    throw new ApiError(0, { title: 'Keine Verbindung zum Server.' });
  }

  if (response.status === 204) return null;
  const data = await response.json().catch(() => null);
  if (!response.ok) throw new ApiError(response.status, data);
  return data;
}

export const api = {
  getApplications: () => request('GET', '/api/applications'),
  getApplication: (id) => request('GET', `/api/applications/${id}`),
  createApplication: (input) => request('POST', '/api/applications', input),
};
