import { useEffect, useRef } from 'react';

/**
 * Calls `tick` every `ms` while the tab is visible and once when the tab becomes visible again; a hidden tab does
 * not poll at all (LIVE-REFRESH: "no polling when the tab is hidden").
 *
 * ⚠ `ms = null` — the poll is off (nothing to ask, or the server said the resource is gone for this user).
 * ⚠ The tick is read through a ref: a fresh callback each render does not restart the interval.
 */
export function usePollWhileVisible(tick: () => void | Promise<void>, ms: number | null): void {
  const latest = useRef(tick);
  latest.current = tick;

  useEffect(() => {
    if (ms === null) return undefined;

    const run = (): void => {
      if (document.visibilityState === 'visible') void latest.current();
    };

    const timer = window.setInterval(run, ms);
    document.addEventListener('visibilitychange', run);

    return () => {
      window.clearInterval(timer);
      document.removeEventListener('visibilitychange', run);
    };
  }, [ms]);
}
