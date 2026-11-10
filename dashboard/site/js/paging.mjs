// Follows `nextCursor` until the last page. The caller passes a function that requests one page, so every
// request still goes through request('<METHOD>', '<path>', ...) where the contract test can see it.

/**
 * @param {(cursor: string | undefined) => Promise<{ data: unknown[], nextCursor: string | null }>} fetchPage
 * @param {{ maxPages?: number }} [options] A guard against a cursor that never ends.
 * @returns {Promise<unknown[]>} The items of every page, in order.
 */
export async function collectPages(fetchPage, { maxPages = 50 } = {}) {
  const items = [];
  let cursor;
  for (let page = 0; page < maxPages; page++) {
    const result = await fetchPage(cursor);
    items.push(...(result?.data ?? []));
    cursor = result?.nextCursor ?? null;
    if (!cursor) {
      return items;
    }
  }
  throw new Error(`Stopped after ${maxPages} pages.`);
}
