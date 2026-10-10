import type { JSX, ReactNode } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { renderHook } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import {
  cancelAutosave,
  holdRejectedEdits,
  noteSaveSucceeded,
  resetFailedSaves,
  useDocumentPending,
} from '../autosave';
import { putPendingEdit, putPendingEdits, resetPending } from '../pendingStore';
import type { PendingEdit } from '../useCellPatch';

/**
 * `G1-03`: закриття вкладки, коли маячок НЕ доїде, — рідне питання браузера,
 * а не мовчазна втрата:
 * - пакет більший за ліміт `keepalive` (64 КиБ): запит падає одразу;
 * - останнє збереження зрізу впало мережею/`5xx`: маячок пішов би до того
 *   самого недоступного сервера.
 */

function wrapper({ children }: { children: ReactNode }): JSX.Element {
  return <QueryClientProvider client={new QueryClient()}>{children}</QueryClientProvider>;
}

function unloadEvent(): Event {
  return new Event('beforeunload', { cancelable: true });
}

const one: PendingEdit = { rowKey: 'r1', columnCode: 'C1', value: '5', isEmpty: false, baseVersion: 'v1' };

afterEach(() => {
  cancelAutosave();
  resetFailedSaves();
  resetPending();
  vi.unstubAllGlobals();
});

describe('G1-03: beforeunload, коли маячок не доїде', () => {
  it('пакет понад 64 КиБ: питання браузера, маячка немає', () => {
    const fetchMock = vi.fn(() => Promise.resolve(new Response('{}')));
    vi.stubGlobal('fetch', fetchMock);
    renderHook(() => useDocumentPending(1), { wrapper });

    const many: PendingEdit[] = Array.from({ length: 2000 }, (_, index) => ({
      rowKey: `00000000-0000-0000-0000-${String(index).padStart(12, '0')}`,
      columnCode: 'C1',
      value: '12345.6789',
      isEmpty: false,
      baseVersion: 'AAAAAAAAB9E=',
    }));
    putPendingEdits(4, 202609, many);

    const event = unloadEvent();
    window.dispatchEvent(event);

    expect(event.defaultPrevented).toBe(true);
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('останнє збереження впало мережею: питання браузера; після успіху — знову маячок', () => {
    const fetchMock = vi.fn(() => Promise.resolve(new Response('{}')));
    vi.stubGlobal('fetch', fetchMock);
    renderHook(() => useDocumentPending(1), { wrapper });

    putPendingEdit(4, 202609, one);
    expect(holdRejectedEdits(4, 202609, new TypeError('Failed to fetch'), [one])).toBe(false);

    const failed = unloadEvent();
    window.dispatchEvent(failed);
    expect(failed.defaultPrevented).toBe(true);
    expect(fetchMock).not.toHaveBeenCalled();

    // Наступне збереження зрізу дійшло (правка лишилась — скажімо, новіша).
    noteSaveSucceeded(4, 202609);

    const ok = unloadEvent();
    window.dispatchEvent(ok);
    expect(ok.defaultPrevented).toBe(false);
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it('малий пакет без відмов — як і раніше, маячок без питання (D-134)', () => {
    const fetchMock = vi.fn(() => Promise.resolve(new Response('{}')));
    vi.stubGlobal('fetch', fetchMock);
    renderHook(() => useDocumentPending(1), { wrapper });

    putPendingEdit(4, 202609, one);

    const event = unloadEvent();
    window.dispatchEvent(event);

    expect(event.defaultPrevented).toBe(false);
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });
});
