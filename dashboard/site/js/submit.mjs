// One Idempotency-Key per submission. While no response has arrived, submitting the same body again reuses the
// key, so the API replays the first result instead of acting twice. Any HTTP response, success or problem, ends
// the submission; the next one gets a new key.

/**
 * @param {{ newKey?: () => string }} [options]
 */
export function createSubmission({ newKey = () => crypto.randomUUID() } = {}) {
  let pending = null;

  return {
    /**
     * @template T
     * @param {unknown} body The request body; a different body is a new submission.
     * @param {(idempotencyKey: string) => Promise<T>} send Sends the request with the key.
     * @returns {Promise<T>}
     */
    async run(body, send) {
      const fingerprint = JSON.stringify(body ?? null);
      if (!pending || pending.fingerprint !== fingerprint) {
        pending = { key: newKey(), fingerprint };
      }
      const { key } = pending;
      try {
        const result = await send(key);
        pending = null;
        return result;
      } catch (error) {
        // Only a request that got no answer may be retried with the same key.
        if (!error?.unreachable) {
          pending = null;
        }
        throw error;
      }
    },

    get pendingKey() {
      return pending?.key ?? null;
    },
  };
}
