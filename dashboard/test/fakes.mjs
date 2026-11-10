// Test doubles shared by the dashboard tests: storage, fetch and timers.

export class MemoryStorage {
  #items = new Map();
  writes = [];

  getItem(name) {
    return this.#items.has(name) ? this.#items.get(name) : null;
  }

  setItem(name, value) {
    this.writes.push(name);
    this.#items.set(name, String(value));
  }

  removeItem(name) {
    this.#items.delete(name);
  }

  get size() {
    return this.#items.size;
  }
}

export function json(status, body, contentType = 'application/json') {
  return new Response(body === undefined ? null : JSON.stringify(body), { status, headers: { 'Content-Type': contentType } });
}

export function problem(status, title, detail, errors) {
  return json(status, { type: `https://cheaphours.dev/errors/x`, title, status, detail, errors }, 'application/problem+json');
}

/** A fetch that answers from a list of responders and records every call. */
export function fakeFetch(...responders) {
  const calls = [];
  const fn = async (url, init) => {
    calls.push({ url, ...init, headers: { ...init.headers } });
    const next = responders.length > 1 ? responders.shift() : responders[0];
    if (next instanceof Error) {
      throw next;
    }
    return typeof next === 'function' ? next(url, init) : next.clone();
  };
  fn.calls = calls;
  return fn;
}

/** Timers on a virtual clock; `advance` runs due callbacks and lets their promises settle. */
export class FakeTimers {
  now = 0;
  #queue = [];
  #id = 0;

  setTimeout = (callback, ms) => {
    const id = ++this.#id;
    this.#queue.push({ id, at: this.now + ms, callback });
    return id;
  };

  clearTimeout = (id) => {
    this.#queue = this.#queue.filter((t) => t.id !== id);
  };

  get pending() {
    return this.#queue.length;
  }

  async advance(ms) {
    const end = this.now + ms;
    for (;;) {
      await settle();
      this.#queue.sort((a, b) => a.at - b.at);
      const next = this.#queue[0];
      if (!next || next.at > end) {
        break;
      }
      this.#queue.shift();
      this.now = next.at;
      next.callback();
    }
    this.now = end;
    await settle();
  }
}

export async function settle() {
  for (let i = 0; i < 10; i++) {
    await new Promise((resolve) => setImmediate(resolve));
  }
}
