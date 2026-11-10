// The dashboard's only HTTP client. Every /v1 call goes through request('<METHOD>', '<path>', ...), which the
// contract test checks against docs/api/openapi.v1.json. The key lives in session storage and travels only in
// the X-Api-Key header; nothing here logs a request.

export const KEY_STORAGE_ITEM = 'cheaphours.apiKey';

const UNREACHABLE_STATUSES = new Set([502, 503, 504]);

/** A failed request: a Problem Details response, or no response at all (`unreachable`). */
export class ApiError extends Error {
  constructor({ status, title, detail = null, errors = {}, type = null, unreachable = false }) {
    super(title);
    this.name = 'ApiError';
    this.status = status;
    this.title = title;
    this.detail = detail;
    this.errors = errors;
    this.type = type;
    this.unreachable = unreachable;
  }
}

/**
 * Creates the client.
 * @param {object} deps
 * @param {typeof fetch} deps.fetch The fetch function (the browser's in the page, a fake in tests).
 * @param {Storage} deps.storage Session storage.
 * @param {() => void} [deps.onUnauthorized] Called when the key is missing or the API answers 401.
 */
export function createClient({ fetch: send, storage, onUnauthorized = () => {} }) {
  const readKey = () => {
    try {
      return storage.getItem(KEY_STORAGE_ITEM);
    } catch {
      return null;
    }
  };

  const dropKey = () => {
    try {
      storage.removeItem(KEY_STORAGE_ITEM);
    } catch {
      // Storage blocked: nothing was kept.
    }
  };

  return {
    hasKey: () => Boolean(readKey()),

    setKey(key) {
      const trimmed = String(key ?? '').trim();
      if (!trimmed) {
        throw new Error('An API key is required.');
      }
      storage.setItem(KEY_STORAGE_ITEM, trimmed);
    },

    forgetKey: dropKey,

    /**
     * Sends one request to the API.
     * @param {'GET'|'POST'|'PATCH'} method
     * @param {string} path A /v1 path built from ids only; query values go in `query`.
     * @param {{ query?: Record<string, unknown>, body?: unknown, idempotencyKey?: string }} [options]
     * @returns {Promise<any>} The parsed JSON body, or null when there is none.
     */
    async request(method, path, { query, body, idempotencyKey } = {}) {
      if (!path.startsWith('/v1/') && path !== '/v1') {
        throw new Error('Only /v1 paths are called.');
      }

      const key = readKey();
      if (!key) {
        onUnauthorized();
        throw new ApiError({ status: 401, title: 'An API key is required.' });
      }

      const headers = { Accept: 'application/json, application/problem+json', 'X-Api-Key': key };
      if (body !== undefined) {
        headers['Content-Type'] = 'application/json';
      }
      if (idempotencyKey) {
        headers['Idempotency-Key'] = idempotencyKey;
      }

      let response;
      try {
        response = await send(withQuery(path, query), {
          method,
          headers,
          body: body === undefined ? undefined : JSON.stringify(body),
          cache: 'no-store',
          credentials: 'same-origin',
          redirect: 'error',
        });
      } catch {
        throw unreachable();
      }

      if (response.status === 401) {
        dropKey();
        onUnauthorized();
        throw await problemOf(response);
      }

      if (!response.ok) {
        throw await problemOf(response);
      }

      const text = await response.text();
      return text ? JSON.parse(text) : null;
    },
  };
}

/** Appends the defined query values; null and undefined are left out. */
export function withQuery(path, query) {
  if (!query) {
    return path;
  }
  const params = new URLSearchParams();
  for (const [name, value] of Object.entries(query)) {
    if (value !== undefined && value !== null && value !== '') {
      params.append(name, String(value));
    }
  }
  const text = params.toString();
  return text ? `${path}?${text}` : path;
}

function unreachable() {
  return new ApiError({ status: 0, title: 'API unreachable', detail: 'No answer from the API. Is the API role running?', unreachable: true });
}

async function problemOf(response) {
  const type = response.headers?.get?.('Content-Type') ?? '';
  if (type.includes('json')) {
    try {
      const problem = await response.json();
      return new ApiError({
        status: response.status,
        title: typeof problem.title === 'string' ? problem.title : `HTTP ${response.status}`,
        detail: typeof problem.detail === 'string' ? problem.detail : null,
        errors: problem.errors && typeof problem.errors === 'object' ? problem.errors : {},
        type: typeof problem.type === 'string' ? problem.type : null,
      });
    } catch {
      // Not readable JSON: fall through.
    }
  }

  // The web server answers 502-504 itself when the API role is not running.
  if (UNREACHABLE_STATUSES.has(response.status)) {
    return unreachable();
  }
  return new ApiError({ status: response.status, title: `HTTP ${response.status}` });
}
