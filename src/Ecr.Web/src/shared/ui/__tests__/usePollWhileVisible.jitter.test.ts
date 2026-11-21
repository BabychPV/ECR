import { afterEach, describe, expect, it, vi } from 'vitest';
import { renderHook } from '@testing-library/react';
import { usePollWhileVisible } from '../usePollWhileVisible';

/** PERF-16: with a spread, ticks of different clients land at different moments; without one the beat is fixed. */
afterEach(() => {
  vi.useRealTimers();
  vi.restoreAllMocks();
});

describe('usePollWhileVisible jitter', () => {
  it.each([
    [0, 24_000],
    [0.999, 36_000],
  ])('random=%s: first tick of a 30 s beat at ~%i ms (±20 %)', async (random, expectedMs) => {
    vi.useFakeTimers();
    vi.spyOn(Math, 'random').mockReturnValue(random);
    const tick = vi.fn();
    renderHook(() => usePollWhileVisible(tick, 30_000, 0.2));

    await vi.advanceTimersByTimeAsync(expectedMs - 100);
    expect(tick).not.toHaveBeenCalled();
    await vi.advanceTimersByTimeAsync(200);
    expect(tick).toHaveBeenCalledTimes(1);
  });

  it('without jitter the beat is exactly the interval', async () => {
    vi.useFakeTimers();
    const tick = vi.fn();
    renderHook(() => usePollWhileVisible(tick, 30_000));

    await vi.advanceTimersByTimeAsync(29_900);
    expect(tick).not.toHaveBeenCalled();
    await vi.advanceTimersByTimeAsync(200);
    expect(tick).toHaveBeenCalledTimes(1);
    await vi.advanceTimersByTimeAsync(30_000);
    expect(tick).toHaveBeenCalledTimes(2);
  });
});
