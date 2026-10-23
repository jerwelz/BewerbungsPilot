// All communication with the backend happens in this file.
// Every function sends one HTTP request and returns the parsed JSON.
//
// Login: the browser sends the login cookie automatically. Requests that change data also need
// the antiforgery token (header X-CSRF-TOKEN), which is fetched from /api/auth/antiforgery.
// 401 (not or no longer logged in) → start page, which forwards to the login page without demo mode.

export class ApiError extends Error {
  constructor(status, data) {
    super(data?.title ?? `Der Server hat mit Fehler ${status} geantwortet.`);
    this.status = status;
    this.errors = data?.errors ?? {}; // field name → list of messages
    this.code = data?.code ?? null;   // e.g. "csrf"
  }
}

let csrfToken = null;
let pageUserId = null; // the user this page was loaded for (set by session.js)

export function setPageUser(userId) {
  pageUserId = userId;
}

// returnUrl only to our own pages ("/…", never "//other.site" or "/\other.site").
export function safeReturnUrl(value) {
  return typeof value === 'string' && value.startsWith('/') && !value.startsWith('//') && !value.startsWith('/\\') ? value : '/';
}

export function redirectToLogin() {
  document.querySelector('.main')?.setAttribute('hidden', ''); // never leave private data on screen
  const query = new URLSearchParams({ returnUrl: location.pathname + location.search });
  if (pageUserId) query.set('expired', '1'); // the page was logged in before: the session has ended
  // The start page shows the demo to visitors; without demo mode it forwards to login.html at once.
  location.replace(`/willkommen.html?${query}`);
}

async function loadCsrfToken() {
  const data = await request('GET', '/api/auth/antiforgery');
  // Another user logged in since this page was loaded (e.g. in another tab): never save this
  // page's content into that account – reload instead.
  if (pageUserId && data.userId !== pageUserId) {
    location.reload();
    throw new ApiError(409, { title: 'Sie wurden in einem anderen Tab abgemeldet oder ein anderes Konto wurde angemeldet.' });
  }
  csrfToken = data.token;
  return data;
}

async function request(method, url, body, { redirectOn401 = true, retry = true } = {}) {
  const changesData = method !== 'GET' && method !== 'HEAD';
  if (changesData && !csrfToken) await loadCsrfToken();

  const options = { method, headers: {} };
  if (changesData) options.headers['X-CSRF-TOKEN'] = csrfToken;
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

  if (response.status === 401 && redirectOn401) {
    redirectToLogin();
    throw new ApiError(401, { title: 'Bitte melden Sie sich an.' });
  }

  // The antiforgery token belongs to one user. If another user has logged in meanwhile
  // (e.g. in another tab), never retry with that account – reload the page instead.
  if (response.status === 403 && data?.code === 'csrf' && retry) {
    csrfToken = null;
    if (pageUserId) {
      const current = await checkSession();
      if (current?.id !== pageUserId) {
        location.reload();
        throw new ApiError(403, data);
      }
    }
    return request(method, url, body, { redirectOn401, retry: false });
  }

  if (response.status === 403 && !data?.title) {
    throw new ApiError(403, { title: 'Keine Berechtigung für diese Aktion.' });
  }
  if (!response.ok) throw new ApiError(response.status, data);
  return data;
}

// The logged-in user, or null – without redirecting (login page, session checks).
async function checkSession() {
  try {
    return await request('GET', '/api/auth/me', undefined, { redirectOn401: false });
  } catch (error) {
    if (error.status === 401) return null;
    throw error;
  }
}

export const api = {
  // ---------- Account ----------
  me: () => request('GET', '/api/auth/me'), // → { id, email, isDemo }; redirects to the login page if needed
  checkSession,
  getAuthOptions: () => loadCsrfToken(), // → { token, allowRegistration, demoEnabled }
  login: async (input) => { // { email, password, rememberMe }
    const user = await request('POST', '/api/auth/login', input, { redirectOn401: false });
    csrfToken = null; // the token is bound to the user
    return user;
  },
  register: async (input) => { // { email, password }
    const user = await request('POST', '/api/auth/register', input, { redirectOn401: false });
    csrfToken = null;
    return user;
  },
  logout: async () => {
    try {
      await request('POST', '/api/auth/logout', undefined, { redirectOn401: false });
    } finally {
      csrfToken = null;
    }
  },
  startDemo: async () => { // demo mode only: new demo account with fictional data, logged in
    const user = await request('POST', '/api/demo/start', undefined, { redirectOn401: false });
    csrfToken = null;
    return user;
  },
  changePassword: (input) => request('POST', '/api/auth/password', input), // { currentPassword, newPassword }

  // ---------- Applications ----------
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
