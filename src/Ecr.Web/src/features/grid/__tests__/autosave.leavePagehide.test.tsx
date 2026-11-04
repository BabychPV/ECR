import type { JSX, ReactNode } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { renderHook } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import {
  abandonSwitchedSession,
  isSessionClosed,
  resetSignOutForTests,
  setReloadPageForTests,
} from '@/api/client';
import { cancelAutosave, registerSliceSaver, scheduleAutosave, useDocumentPending } from '../autosave';
import { markPendingRejected, putPendingEdit, resetPending } from '../pendingStore';
import type { PendingEdit } from '../useCellPatch';

/**
 * L8-08, відповідь «Піти»: браузер питає «Покинути сторінку?» після `beforeunload`; при «Піти»
 * звичайний збір (`setTimeout 0`) не встигає, а маячка не було - правильні правки гинули. Тепер
 * маячок їде з `pagehide`, але лише ПІСЛЯ поставленого питання й без утриманих правок.
 */

const held: PendingEdit = { rowKey: 'r1', columnCode: 'C1', value: 'abc', isEmpty: false, baseVersion: 'v1' };
const good: PendingEdit = { rowKey: 'r2', columnCode: 'C1', value: 5, isEmpty: false, baseVersion: 'v1' };

function wrapper({ children }: { children: ReactNode }): JSX.Element {
  return <QueryClientProvider client={new QueryClient()}>{children}</QueryClientProvider>;
}

type FetchCall = [string, RequestInit | undefined];

function beacons(fetchMock: { mock: { calls: unknown[][] } }): FetchCall[] {
  return (fetchMock.mock.calls as FetchCall[]).filter((call) => call[1]?.keepalive === true);
}

function beaconCells(fetchMock: { mock: { calls: unknown[][] } }): string[] {
  return beacons(fetchMock).flatMap((call) => {
    const body = JSON.parse(String(call[1]?.body)) as { rows: { rowKey: string }[] };

    return body.rows.map((row) => row.rowKey);
  });
}

function setup(): { fetchMock: ReturnType<typeof vi.fn>; saved: unknown[]; off: () => void } {
  vi.useFakeTimers();
  const fetchMock = vi.fn((..._args: unknown[]) => Promise.resolve(new Response('{}')));
  vi.stubGlobal('fetch', fetchMock);
  renderHook(() => useDocumentPending(1), { wrapper });
  const saved: unknown[] = [];
  const off = registerSliceSaver(4, 202609, (edits) => saved.push(...edits));

  return { fetchMock, saved, off };
}

function beforeUnload(): Event {
  const event = new Event('beforeunload', { cancelable: true });
  window.dispatchEvent(event);

  return event;
}

afterEach(() => {
  cancelAutosave();
  resetPending();
  vi.unstubAllGlobals();
  vi.useRealTimers();
});

describe('L8-08: «Піти» - маячок із pagehide', () => {
  it('утримана + правильна правка, «Піти»: beforeunload питає без маячка, pagehide везе лише правильну', () => {
    const { fetchMock, off } = setup();
    putPendingEdit(4, 202609, held);
    putPendingEdit(4, 202609, good);
    markPendingRejected(4, 202609, [{ edit: held, message: 'bad', scope: 'cell' }]);

    expect(beforeUnload().defaultPrevented).toBe(true);
    expect(beacons(fetchMock)).toHaveLength(0);

    window.dispatchEvent(new Event('pagehide'));

    // ⛔ Мутація: прибрати `pagehide`-слухача - маячків нуль, правильна правка гине.
    expect(beaconCells(fetchMock)).toEqual(['r2']);
    off();
  });

  it('«Залишитися»: pagehide не настає - маячка немає, таймер збирає звичайно', () => {
    const { fetchMock, saved, off } = setup();
    putPendingEdit(4, 202609, held);
    putPendingEdit(4, 202609, good);
    markPendingRejected(4, 202609, [{ edit: held, message: 'bad', scope: 'cell' }]);

    expect(beforeUnload().defaultPrevented).toBe(true);
    vi.runAllTimers();
    expect(saved).toEqual([good]);

    // ⛔ Мутація: слати маячок із самого beforeunload (як раніше пропонувалось) - тут він з'явиться.
    expect(beacons(fetchMock)).toHaveLength(0);
    off();
  });

  it('збір під час діалогу вже зберіг правку (сховище порожнє): pagehide нічого не шле', () => {
    const { fetchMock, off } = setup();
    putPendingEdit(4, 202609, held);
    putPendingEdit(4, 202609, good);
    markPendingRejected(4, 202609, [{ edit: held, message: 'bad', scope: 'cell' }]);

    expect(beforeUnload().defaultPrevented).toBe(true);
    // Збір, що спрацював поки діалог відкритий, знімає збережене зі сховища.
    resetPending();
    putPendingEdit(4, 202609, held);
    markPendingRejected(4, 202609, [{ edit: held, message: 'bad', scope: 'cell' }]);
    window.dispatchEvent(new Event('pagehide'));

    expect(beacons(fetchMock)).toHaveLength(0);
    off();
  });

  it('pagehide без питання (інший сценарій вивантаження): маячок не їде з нього', () => {
    const { fetchMock, off } = setup();
    putPendingEdit(4, 202609, good);

    window.dispatchEvent(new Event('pagehide'));

    expect(beacons(fetchMock)).toHaveLength(0);
    off();
  });

  it('лише утримані правки, «Піти»: надсилати нічого', () => {
    const { fetchMock, off } = setup();
    putPendingEdit(4, 202609, held);
    markPendingRejected(4, 202609, [{ edit: held, message: 'bad', scope: 'cell' }]);

    expect(beforeUnload().defaultPrevented).toBe(true);
    window.dispatchEvent(new Event('pagehide'));

    expect(fetchMock).not.toHaveBeenCalled();
    off();
  });

  it('шлях без питання: маячок уже пішов у beforeunload, pagehide не дублює його', () => {
    const { fetchMock, off } = setup();
    putPendingEdit(4, 202609, good);

    expect(beforeUnload().defaultPrevented).toBe(false);
    expect(beacons(fetchMock)).toHaveLength(1);

    window.dispatchEvent(new Event('pagehide'));

    expect(beacons(fetchMock)).toHaveLength(1);
    off();
  });

  it('запит у дорозі: «Піти» везе лише рядки, чий PATCH не летить', () => {
    vi.useFakeTimers();
    const fetchMock = vi.fn((..._args: unknown[]) => new Promise<Response>(() => {}));
    vi.stubGlobal('fetch', fetchMock);
    renderHook(() => useDocumentPending(1), { wrapper });

    putPendingEdit(4, 202609, held);
    scheduleAutosave();
    vi.runOnlyPendingTimers();
    putPendingEdit(4, 202609, good);

    expect(beforeUnload().defaultPrevented).toBe(true);
    window.dispatchEvent(new Event('pagehide'));

    // r1 летить (стара версія в кеші) - його не чіпаємо; r2 їде.
    expect(beaconCells(fetchMock)).toEqual(['r2']);
  });

  it('сеанс закрито (AN-108): «Піти» нічого не шле', () => {
    const { fetchMock, off } = setup();
    setReloadPageForTests(vi.fn());
    putPendingEdit(4, 202609, held);
    putPendingEdit(4, 202609, good);
    markPendingRejected(4, 202609, [{ edit: held, message: 'bad', scope: 'cell' }]);

    expect(beforeUnload().defaultPrevented).toBe(true);
    abandonSwitchedSession();
    window.dispatchEvent(new Event('pagehide'));

    expect(isSessionClosed()).toBe(true);
    expect(fetchMock).not.toHaveBeenCalled();
    resetSignOutForTests();
    off();
  });
});
