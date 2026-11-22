import { useEffect, useRef } from 'react';
import { randomJittered } from '@/shared/polling/pollBackoff';

/**
 * Calls `tick` every `ms` while the tab is visible and once when the tab becomes visible again; a hidden tab does
 * not poll at all (LIVE-REFRESH: "no polling when the tab is hidden").
 *
 * ⚠ `ms = null` — the poll is off (nothing to ask, or the server said the resource is gone for this user).
 * ⚠ The tick is read through a ref: a fresh callback each render does not restart the interval.
 * ⚠ PERF-16: `jitter` (a fraction, e.g. `0.2` = ±20 %) re-draws every delay, so clients that started together drift
 * apart instead of hitting the server in the same second; `0` (default) keeps the fixed beat.
 */
export function usePollWhileVisible(tick: () => void | Promise<void>, ms: number | null, jitter = 0): void {
  const latest = useRef(tick);
  latest.current = tick;

  useEffect(() => {
    if (ms === null) return undefined;

    const run = (): void => {
      if (document.visibilityState === 'visible') void latest.current();
    };

    let timer: number | undefined;
    const schedule = (): void => {
      const delay = jitter > 0 ? randomJittered(ms, jitter) : ms;
      timer = window.setTimeout(() => {
        run();
        schedule();
      }, delay);
    };
    schedule();
    document.addEventListener('visibilitychange', run);

    return () => {
      window.clearTimeout(timer);
      document.removeEventListener('visibilitychange', run);
    };
  }, [ms, jitter]);
}
