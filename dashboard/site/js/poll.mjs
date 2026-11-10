// Refreshes confirmed commands until each reaches a final state: every 2 s for the first minute, then every
// 30 s, and only while the tab is visible. A command is never requested again once it is final.

export const FINAL_STATES = Object.freeze(['Confirmed', 'Rejected', 'DeadLettered', 'Cancelled']);

export function isFinal(state) {
  return FINAL_STATES.includes(state);
}

/**
 * @param {object} options
 * @param {{ id: string, state: string }[]} options.commands The commands as the confirmation returned them.
 * @param {(id: string) => Promise<{ id: string, state: string }>} options.fetchCommand Reads one command.
 * @param {(command: object) => void} options.onUpdate Called with each command read.
 * @param {(id: string, error: unknown) => void} [options.onError] Called when a read fails; the command stays followed.
 * @param {{ setTimeout: Function, clearTimeout: Function }} [options.timers]
 * @param {() => number} [options.now] Milliseconds, for the switch from the fast to the slow interval.
 * @param {() => boolean} [options.isVisible] Whether the tab is visible.
 * @returns {{ stop: () => void, open: () => string[] }}
 */
export function followCommands({
  commands,
  fetchCommand,
  onUpdate,
  onError = () => {},
  timers = globalThis,
  now = () => Date.now(),
  isVisible = () => true,
  fastMs = 2_000,
  fastForMs = 60_000,
  slowMs = 30_000,
}) {
  const open = new Set(commands.filter((c) => !isFinal(c.state)).map((c) => c.id));
  const started = now();
  let handle = null;
  let stopped = false;

  const stop = () => {
    stopped = true;
    if (handle !== null) {
      timers.clearTimeout(handle);
      handle = null;
    }
  };

  const schedule = () => {
    if (stopped || open.size === 0) {
      stop();
      return;
    }
    const delay = now() - started < fastForMs ? fastMs : slowMs;
    handle = timers.setTimeout(tick, delay);
  };

  const tick = async () => {
    handle = null;
    if (stopped) {
      return;
    }
    if (isVisible()) {
      await Promise.all(
        [...open].map(async (id) => {
          try {
            const command = await fetchCommand(id);
            if (stopped) {
              return;
            }
            onUpdate(command);
            if (isFinal(command.state)) {
              open.delete(id);
            }
          } catch (error) {
            onError(id, error);
          }
        }),
      );
    }
    schedule();
  };

  schedule();
  return { stop, open: () => [...open] };
}
