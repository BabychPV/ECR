import { afterEach, describe, expect, it, vi } from 'vitest';
import { renderHook } from '@testing-library/react';

const show = vi.fn();
vi.mock('@mantine/notifications', () => ({ notifications: { show: (...args: unknown[]) => show(...args) } }));

import { useStaleResultsReminder, useStaleResultsReminderHost } from '@/features/documents/useStaleResultsReminder';

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

  it('N3-08: розмонтування рядка дій (зміна періоду) не нагадує - лише вихід власника-сторінки', () => {
    // Сторінка (власник) і рядок дій (звітує) - як `DocumentPage` + `DocumentActionBar` під `AsyncBoundary`.
    const page = renderHook(() => useStaleResultsReminderHost(5));
    const bar = renderHook(() => useStaleResultsReminder(5, true, true, page.result.current));

    // Період змінено: `AsyncBoundary` розмонтував рядок, сторінка жива.
    bar.unmount();
    expect(show).not.toHaveBeenCalled();

    // Новий рядок звітує стан нового періоду, і лише вихід із документа нагадує.
    const next = renderHook(() => useStaleResultsReminder(5, true, true, page.result.current));
    next.unmount();
    expect(show).not.toHaveBeenCalled();

    page.unmount();
    expect(show).toHaveBeenCalledTimes(1);
    expect(show.mock.calls[0]?.[0]).toMatchObject({ id: 'stale-results-5' });
  });

  it('N3-08: період без застарілих результатів - при виході з документа тиша', () => {
    const page = renderHook(() => useStaleResultsReminderHost(5));
    const old = renderHook(() => useStaleResultsReminder(5, true, true, page.result.current));
    old.unmount();
    renderHook(() => useStaleResultsReminder(5, false, true, page.result.current));

    page.unmount();

    expect(show).not.toHaveBeenCalled();
  });
});
