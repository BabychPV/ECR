import { afterEach, describe, expect, it, vi } from 'vitest';
import { renderHook } from '@testing-library/react';

const show = vi.fn();
vi.mock('@mantine/notifications', () => ({ notifications: { show: (...args: unknown[]) => show(...args) } }));

import { useStaleResultsReminder } from '@/features/documents/useStaleResultsReminder';

/**
 * Нагадування при виході з документа із застарілими результатами.
 * Мутація: приберіть перевірку `canRecalculate` або `stale` у `useStaleResultsReminder.ts` - відповідний тест червоний.
 */
afterEach(() => {
  show.mockClear();
});

describe('useStaleResultsReminder', () => {
  it('при виході зі застарілого документа ролі з правом показує тост', () => {
    const { unmount } = renderHook(() => useStaleResultsReminder(5, true, true));
    expect(show).not.toHaveBeenCalled();

    unmount();

    expect(show).toHaveBeenCalledTimes(1);
    expect(show.mock.calls[0]?.[0]).toMatchObject({ id: 'stale-results-5' });
  });

  it('не нагадує, коли результати свіжі або роль не може перерахувати', () => {
    renderHook(() => useStaleResultsReminder(5, false, true)).unmount();
    renderHook(() => useStaleResultsReminder(5, true, false)).unmount();

    expect(show).not.toHaveBeenCalled();
  });

  it('бере значення на момент виходу: застаріло вже після першого рендеру', () => {
    const { rerender, unmount } = renderHook(({ stale }) => useStaleResultsReminder(5, stale, true), {
      initialProps: { stale: false },
    });
    rerender({ stale: true });

    unmount();

    expect(show).toHaveBeenCalledTimes(1);
  });
});
