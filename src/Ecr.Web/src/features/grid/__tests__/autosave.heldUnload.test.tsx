import type { JSX, ReactNode } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { renderHook } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { cancelAutosave, registerUnloadFlush, useDocumentPending } from '../autosave';
import { markPendingRejected, putPendingEdit, resetPending } from '../pendingStore';
import type { PendingEdit } from '../useCellPatch';

/**
 * AN-39 / L8-08: утримані (відхилені сервером) правки не їдуть маячком, тож закриття
 * вкладки губило їх мовчки. Тепер для них - рідне питання браузера; решта - як і було.
 */

const edit: PendingEdit = { rowKey: 'r1', columnCode: 'C1', value: 'abc', isEmpty: false, baseVersion: 'v1' };
const other: PendingEdit = { rowKey: 'r2', columnCode: 'C1', value: 5, isEmpty: false, baseVersion: 'v1' };

function wrapper({ children }: { children: ReactNode }): JSX.Element {
  return <QueryClientProvider client={new QueryClient()}>{children}</QueryClientProvider>;
}

function unloadEvent(): Event {
  return new Event('beforeunload', { cancelable: true });
}

afterEach(() => {
  cancelAutosave();
  resetPending();
  vi.unstubAllGlobals();
});

describe('L8-08: beforeunload і утримані правки', () => {
  it('flush повертає true -> preventDefault (рідне питання браузера)', () => {
    const event = unloadEvent();
    const off = registerUnloadFlush(() => true, () => true);
    window.dispatchEvent(event);
    off();

    expect(event.defaultPrevented).toBe(true);
  });

  it('утримана правка в сховищі: закриття вкладки питає; правильна правка іде маячком', () => {
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(new Response('{}'))));
    renderHook(() => useDocumentPending(1), { wrapper });

    putPendingEdit(4, 202609, edit);
    putPendingEdit(4, 202609, other);
    markPendingRejected(4, 202609, [{ edit, message: 'bad', scope: 'cell' }]);

    const event = unloadEvent();
    window.dispatchEvent(event);

    expect(event.defaultPrevented).toBe(true);
  });

  it('лише правильні правки: діалогу немає (поведінка D-134 збережена)', () => {
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(new Response('{}'))));
    renderHook(() => useDocumentPending(1), { wrapper });

    putPendingEdit(4, 202609, other);

    const event = unloadEvent();
    window.dispatchEvent(event);

    expect(event.defaultPrevented).toBe(false);
  });
});
