import { describe, expect, it } from 'vitest';
import { ReopenExpirySpreadMs, reopenExpiryDelayMs } from '../useDocumentPeriodReopen';

/** PERF-16: viewers of one period do not all refetch at the same wall-clock second when the window ends. */
describe('reopenExpiryDelayMs', () => {
  it('adds 0..30 s on top of the remaining time + 1 s', () => {
    expect(reopenExpiryDelayMs(60_000, () => 0)).toBe(61_000);
    expect(reopenExpiryDelayMs(60_000, () => 0.5)).toBe(76_000);
    expect(reopenExpiryDelayMs(60_000, () => 0.999_999_9)).toBeLessThan(61_000 + ReopenExpirySpreadMs);
  });

  it('an already expired window still waits 1 s plus the spread; the timer cap holds', () => {
    expect(reopenExpiryDelayMs(-5, () => 0)).toBe(1_000);
    expect(reopenExpiryDelayMs(Number.MAX_SAFE_INTEGER, () => 0.5)).toBe(2_147_483_647);
  });

  it('1000 viewers spread over the window', () => {
    const values = new Set<number>();
    for (let i = 0; i < 1000; i += 1) values.add(reopenExpiryDelayMs(10_000, () => i / 1000));
    expect(values.size).toBeGreaterThan(500);
  });
});
