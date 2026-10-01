import { act, renderHook } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { usePendingLoading } from '../usePendingLoading';

describe('usePendingLoading (ФВ-14.26)', () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('дія до 100 мс — спінера немає взагалі', () => {
    // ⛔ Мутація «`return pending`» вмикає спінер з першого кадру — червоне тут.
    const { result, rerender } = renderHook(({ pending }) => usePendingLoading(pending), {
      initialProps: { pending: true },
    });

    expect(result.current).toBe(false);

    act(() => vi.advanceTimersByTime(80));
    expect(result.current).toBe(false);

    rerender({ pending: false });
    act(() => vi.advanceTimersByTime(200));
    expect(result.current).toBe(false);
  });

  it('дія довша за 100 мс — спінер є, і гасне в тому ж рендері, де дія скінчилась', () => {
    const { result, rerender } = renderHook(({ pending }) => usePendingLoading(pending), {
      initialProps: { pending: true },
    });

    act(() => vi.advanceTimersByTime(120));
    expect(result.current).toBe(true);

    act(() => vi.advanceTimersByTime(2000));
    expect(result.current).toBe(true);

    rerender({ pending: false });
    expect(result.current).toBe(false);
  });
});
