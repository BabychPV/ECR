import type { JSX, ReactNode } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { renderHook } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { queryKeys } from '@/api/queryKeys';
import { cancelAutosave, useDocumentPending } from '../autosave';
import { cellKey, markPendingRejected, pendingSlice, putPendingEdit, resetPending } from '../pendingStore';
import type { PendingEdit } from '../useCellPatch';

/**
 * bfcache: після «Піти» + «Назад» сторінка відновлюється з правками, що вже поїхали маячком, зі старою
 * версією - наступне збереження дало б `409`. На `pageshow` з `persisted` поїхавше знімається зі
 * сховища (лише незмінене), зрізи інвалідуються; правки ПІСЛЯ маячка й утримані лишаються.
 */

const sent: PendingEdit = { rowKey: 'r1', columnCode: 'C1', value: 5, isEmpty: false, baseVersion: 'v1' };
const held: PendingEdit = { rowKey: 'r2', columnCode: 'C1', value: 'bad', isEmpty: false, baseVersion: 'v1' };

function setup(): { client: QueryClient; invalidate: ReturnType<typeof vi.spyOn> } {
  const client = new QueryClient();
  const invalidate = vi.spyOn(client, 'invalidateQueries');
  vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(new Response('{}'))));
  const wrapper = ({ children }: { children: ReactNode }): JSX.Element => (
    <QueryClientProvider client={client}>{children}</QueryClientProvider>
  );
  renderHook(() => useDocumentPending(1), { wrapper });

  return { client, invalidate };
}

function pageShow(persisted: boolean): void {
  window.dispatchEvent(new PageTransitionEvent('pageshow', { persisted }));
}

function leave(): void {
  const event = new Event('beforeunload', { cancelable: true });
  window.dispatchEvent(event);
  if (event.defaultPrevented) window.dispatchEvent(new Event('pagehide'));
}

const has = (edit: PendingEdit): boolean => pendingSlice(4, 202609).has(cellKey(edit));

afterEach(() => {
  cancelAutosave();
  resetPending();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('bfcache: pageshow persisted після маячка', () => {
  it('шлях без питання: поїхала правка знімається зі сховища, зріз інвалідується', () => {
    const { invalidate } = setup();
    putPendingEdit(4, 202609, sent);

    leave();
    pageShow(true);

    expect(has(sent)).toBe(false);
    expect(invalidate).toHaveBeenCalledWith({ queryKey: queryKeys.slices.one(4, 202609) });
  });

  it('«Піти» (питання + pagehide): поїхала знімається, утримана лишається', () => {
    setup();
    putPendingEdit(4, 202609, sent);
    putPendingEdit(4, 202609, held);
    markPendingRejected(4, 202609, [{ edit: held, message: 'bad', scope: 'cell' }]);

    leave();
    pageShow(true);

    expect(has(sent)).toBe(false);
    expect(has(held)).toBe(true);
  });

  it('правка тієї ж комірки ПІСЛЯ маячка (інше значення) лишається', () => {
    setup();
    putPendingEdit(4, 202609, sent);

    leave();
    putPendingEdit(4, 202609, { ...sent, value: 9 });
    pageShow(true);

    expect(pendingSlice(4, 202609).get(cellKey(sent))?.value).toBe(9);
  });

  it('pageshow без persisted (звичайне завантаження): нічого не чіпає', () => {
    const { invalidate } = setup();
    putPendingEdit(4, 202609, sent);

    leave();
    pageShow(false);

    expect(has(sent)).toBe(true);
    expect(invalidate).not.toHaveBeenCalled();
  });

  it('маячка не було: persisted pageshow нічого не чіпає', () => {
    const { invalidate } = setup();
    pageShow(true);

    expect(invalidate).not.toHaveBeenCalled();
  });
});
