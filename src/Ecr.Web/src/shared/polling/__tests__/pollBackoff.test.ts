import { describe, expect, it } from 'vitest';
import {
  JitterFraction,
  QueuedPollSteps,
  failureBackoffMs,
  jittered,
  stepOf,
  streakOf,
  unchangedBackoffMs,
} from '../pollBackoff';

describe('jittered (PERF-08)', () => {
  it('stays within +-20 % for any key and seed', () => {
    for (let key = 0; key < 2000; key += 1) {
      const ms = jittered(10_000, key, JitterFraction, (key % 97) / 97);
      expect(ms).toBeGreaterThanOrEqual(8_000);
      expect(ms).toBeLessThanOrEqual(12_000);
    }
  });

  it('is deterministic for one key (refetchInterval is evaluated many times per answer)', () => {
    expect(jittered(2_000, 123, 0.2, 0.3)).toBe(jittered(2_000, 123, 0.2, 0.3));
  });

  it('actually spreads clients: 1000 keys give a wide range of values', () => {
    const values = new Set<number>();
    for (let key = 0; key < 1000; key += 1) values.add(jittered(30_000, key, 0.2, 0.5));
    expect(values.size).toBeGreaterThan(500);
  });
});

describe('stepOf / failureBackoffMs', () => {
  it('queue schedule: 2 -> 4 -> 8 -> 15 s, the last step repeats', () => {
    expect([1, 2, 3, 4, 5, 50].map((n) => stepOf(QueuedPollSteps, n))).toEqual([2000, 4000, 8000, 15000, 15000, 15000]);
  });

  it('failure back-off doubles up to the cap', () => {
    expect([0, 1, 2, 3].map((n) => failureBackoffMs(30_000, n, 120_000))).toEqual([30_000, 60_000, 120_000, 120_000]);
  });
});

describe('streakOf', () => {
  it('counts equal answers, resets on a change, and is idempotent per answer', () => {
    const query = {};
    expect(streakOf(query, 1, 'a')).toBe(1);
    expect(streakOf(query, 1, 'a')).toBe(1);
    expect(streakOf(query, 2, 'a')).toBe(2);
    expect(streakOf(query, 3, 'a')).toBe(3);
    expect(streakOf(query, 4, 'b')).toBe(1);
    expect(streakOf(query, 5, null)).toBe(0);
    expect(streakOf(query, 6, 'b')).toBe(1);
  });

  it('unchangedBackoffMs grows while nothing changes and falls back when something does', () => {
    const query = {};
    const at = (n: number, sig: string): number => unchangedBackoffMs(query, n, sig, [1000, 2000, 4000]);
    const near = (ms: number, base: number): void => {
      expect(ms).toBeGreaterThanOrEqual(base * 0.8);
      expect(ms).toBeLessThanOrEqual(base * 1.2);
    };
    near(at(1, 'x'), 1000);
    near(at(2, 'x'), 2000);
    near(at(3, 'x'), 4000);
    near(at(4, 'x'), 4000);
    near(at(5, 'y'), 1000);
  });
});
