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
  if (body instanceof FormData) {
    options.body = body; // file uploads: the browser sets the multipart Content-Type itself
  } else if (body !== undefined) {
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
  getApplication: (id) => request('GET', `/api/applications/${id}`), // → { application, events }
  createApplication: (input) => request('POST', '/api/applications', input),
  updateApplication: (id, input) => request('PUT', `/api/applications/${id}`, input),
  deleteApplication: (id) => request('DELETE', `/api/applications/${id}`),
  changeStatus: (id, status) => request('PATCH', `/api/applications/${id}/status`, { status }),

  findDuplicates: (companyName, position, excludeId) => {
    const query = new URLSearchParams({ companyName, position });
    if (excludeId) query.set('excludeId', excludeId);
    return request('GET', `/api/applications/duplicates?${query}`);
  },

  getDashboard: () => request('GET', '/api/dashboard'),

  uploadDocument: (applicationId, file, category) => {
    const form = new FormData();
    form.append('file', file);
    form.append('category', category);
    return request('POST', `/api/applications/${applicationId}/documents`, form);
  },
  deleteDocument: (id) => request('DELETE', `/api/documents/${id}`),
  createInterview: (applicationId, input) => request('POST', `/api/applications/${applicationId}/interviews`, input),
  updateInterview: (id, input) => request('PUT', `/api/interviews/${id}`, input),
  deleteInterview: (id) => request('DELETE', `/api/interviews/${id}`),
  getCalendar: (from, to) => request('GET', `/api/calendar?from=${from}&to=${to}`), // "YYYY-MM-DD"
  getUpcoming: () => request('GET', '/api/calendar/upcoming'),

  // from/to are optional "YYYY-MM-DD"
  getTimeline: (from, to) => request('GET', from ? `/api/timeline?from=${from}&to=${to}` : '/api/timeline'),
  // { from, to, cutoff } – each optional "YYYY-MM-DD"
  getAnalysis: (params) => {
    const query = new URLSearchParams(Object.entries(params).filter(([, value]) => value));
    return request('GET', `/api/analysis?${query}`);
  },
  // Map: the starting point goes in the request body (never in the URL), see MapEndpoints.cs
  queryMap: (origin) => request('POST', '/api/map/query', { origin }),
  setMapLocation: (id, input) => request('PUT', `/api/applications/${id}/map-location`, input),
  geocode: (query) => request('POST', '/api/geocode', { query }), // forwarded to Nominatim
  getWaiting: (sort) => request('GET', `/api/waiting?sort=${sort}`), // SinceSubmission | SinceStatusChange

  previewUrl: (id) => `/api/documents/${id}/preview`,
  downloadUrl: (id) => `/api/documents/${id}/download`,
};
