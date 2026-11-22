/** Whether the tab is hidden right now (always `false` outside a browser). */
export function isTabHidden(): boolean {
  return typeof document !== 'undefined' && document.visibilityState === 'hidden';
}

/**
 * Resolves when the tab is visible (at once if it already is).
 *
 * PERF-16: a follower that loops with `await sleep` is not a TanStack interval and does not pause itself in a
 * hidden tab; it waits here before each request instead.
 */
export function untilTabVisible(): Promise<void> {
  if (!isTabHidden()) return Promise.resolve();

  return new Promise((resolve) => {
    const onChange = (): void => {
      if (isTabHidden()) return;
      document.removeEventListener('visibilitychange', onChange);
      resolve();
    };
    document.addEventListener('visibilitychange', onChange);
  });
}
