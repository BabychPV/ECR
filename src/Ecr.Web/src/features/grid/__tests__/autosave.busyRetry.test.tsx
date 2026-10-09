import type { JSX, ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { renderHook } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import {
  cancelAutosave,
  clearBusyRetry,
  isBusyRetryWaiting,
  scheduleAutosave,
  useDocumentPending,
} from '../autosave';
import { hasPending, pendingRejections, putPendingEdit, resetPending, sendableEdits } from '../pendingStore';
import type { PendingEdit } from '../useCellPatch';
import { showApiError } from '@/shared/ui/notify';

vi.mock('@/shared/ui/notify', () => ({ showApiError: vi.fn() }));

/**
 * AN-123 (`R1-03` = `R2-01`): `409 ECR-DOC-4091` з `messageKey` `lockTimeout` — сервер
 * не дочекався блокування (перенос версії, великий імпорт), нічого не записано.
 * Це НЕ остаточна відмова: правки лишаються придатними до надсилання, автозбереження
 * повторює їх саме з відступом, маячок закриття вкладки не блокується, тосту немає.
 *
 * ⛔ До виправлення `rejectionMarksOf` утримував увесь пакет (`V-01`): повтору не було,
 * а закриття вкладки питало «Покинути сторінку?» замість маячка.
 */

const Table = 4;
const Period = 202609;
const edit: PendingEdit = { rowKey: 'r1', columnCode: 'C1', value: 5, isEmpty: false, baseVersion: 'v1' };

function wrapper({ children }: { children: ReactNode }): JSX.Element {
  return <QueryClientProvider client={new QueryClient()}>{children}</QueryClientProvider>;
}

function busy(): Response {
  return new Response(
    JSON.stringify({
      title: 'Зайнято',
      status: 409,
      detail: 'Data is busy',
      errorCode: 'ECR-DOC-4091',
      correlationId: 'c1',
      messageKey: 'err.ECR-DOC-4091.lockTimeout',
      sqlError: '1222',
    }),
    { status: 409, headers: { 'Content-Type': 'application/problem+json' } },
  );
}

function ok(): Response {
  return new Response(JSON.stringify({ appliedCells: 1, rowVersions: { r1: 'v2' }, validation: [] }), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  });
}

/** Сервер: перші `busyCount` PATCH — `409 lockTimeout`, далі `200`. */
function mockServer(busyCount: number): ReturnType<typeof vi.fn> {
  let patches = 0;
  const fetchMock = vi.fn((_path: string, init?: RequestInit) => {
    if (init?.method !== 'PATCH') return Promise.resolve(new Response('{}'));
    patches += 1;

    return Promise.resolve(patches <= busyCount ? busy() : ok());
  });
  vi.stubGlobal('fetch', fetchMock);

  return fetchMock;
}

function patchCalls(fetchMock: ReturnType<typeof vi.fn>): unknown[][] {
  return fetchMock.mock.calls.filter((call) => (call[1] as RequestInit | undefined)?.method === 'PATCH');
}

/** Час на місці; лише довести до кінця відповіді сервера (розбір тіла — проміси). */
async function settle(): Promise<void> {
  for (let i = 0; i < 20; i += 1) await vi.advanceTimersByTimeAsync(0);
}

/** Зсунути годинник на `ms` і дати відповідям дійти. */
async function advance(ms: number): Promise<void> {
  await vi.advanceTimersByTimeAsync(ms);
  await settle();
}

beforeEach(() => {
  vi.useFakeTimers();
  // Розкид відступу — нижня межа (×0.5): тест не залежить від випадковості.
  vi.spyOn(Math, 'random').mockReturnValue(0);
});

afterEach(() => {
  cancelAutosave();
  clearBusyRetry();
  resetPending();
  vi.mocked(showApiError).mockClear();
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
  vi.useRealTimers();
});

describe('AN-123: 409 lockTimeout повторюється з відступом, а не утримується', () => {
  it('перший PATCH 409 lockTimeout, повтор без участі людини — 200; сховище порожнє, тосту немає', async () => {
    const fetchMock = mockServer(1);
    renderHook(() => useDocumentPending(1), { wrapper });

    putPendingEdit(Table, Period, edit);
    scheduleAutosave();
    await advance(500);

    expect(patchCalls(fetchMock)).toHaveLength(1);
    // ⛔ Мутація: прибрати `isTransientBusy` у `rejectionMarksOf` — правка утримана, `sendable` порожній.
    expect(pendingRejections(Table, Period).size).toBe(0);
    expect(sendableEdits(Table, Period)).toEqual([edit]);
    expect(isBusyRetryWaiting()).toBe(true);
    expect(showApiError).not.toHaveBeenCalled();

    // Перший відступ: 5 с × 0.5.
    await advance(2_500);

    expect(patchCalls(fetchMock)).toHaveLength(2);
    expect(hasPending()).toBe(false);
    expect(isBusyRetryWaiting()).toBe(false);
  });

  it('відступ росте: другий повтор не раніше 5 с після другої відмови', async () => {
    const fetchMock = mockServer(2);
    renderHook(() => useDocumentPending(1), { wrapper });

    putPendingEdit(Table, Period, edit);
    scheduleAutosave();
    await advance(500);
    await advance(2_500);
    expect(patchCalls(fetchMock)).toHaveLength(2);

    // Другий відступ: 10 с × 0.5 = 5 с — за 4,9 с повтору ще немає.
    await advance(4_900);
    expect(patchCalls(fetchMock)).toHaveLength(2);

    await advance(100);
    expect(patchCalls(fetchMock)).toHaveLength(3);
    expect(hasPending()).toBe(false);
  });

  // ✎ R5-G1 / G1-03: було «маячок іде, питання немає». Маячок у мить, коли дані
  // зайняті (перенос версії тримає блокування хвилинами), дістає той самий
  // `409 lockTimeout` і губиться мовчки. Тепер — рідне питання браузера і
  // звичайне збереження після нього; правка при цьому НЕ утримана (суть AN-123
  // лишається: `sendable` не порожній, повтор іде сам).
  it('закриття вкладки після 409 lockTimeout: питання браузера, маячка немає, правка не утримана', async () => {
    const fetchMock = mockServer(1);
    renderHook(() => useDocumentPending(1), { wrapper });

    putPendingEdit(Table, Period, edit);
    scheduleAutosave();
    await advance(500);

    const event = new Event('beforeunload', { cancelable: true });
    window.dispatchEvent(event);

    expect(event.defaultPrevented).toBe(true);
    expect(sendableEdits(Table, Period)).toEqual([edit]);
    const beacons = fetchMock.mock.calls.filter(
      (call) => (call[1] as RequestInit | undefined)?.keepalive === true,
    );
    expect(beacons).toHaveLength(0);
  });
});

/** `503 ECR-SYS-0503 databaseBusy` з `Retry-After` — як пише `ExceptionHandlingMiddleware` (E1-04). */
function databaseBusy(retryAfter: string): Response {
  return new Response(
    JSON.stringify({
      title: 'Database temporarily unavailable',
      status: 503,
      errorCode: 'ECR-SYS-0503',
      correlationId: 'c2',
      messageKey: 'err.ECR-SYS-0503.databaseBusy',
    }),
    { status: 503, headers: { 'Content-Type': 'application/problem+json', 'Retry-After': retryAfter } },
  );
}

describe('X8-06: 503 ECR-SYS-0503 databaseBusy повторюється сам, не раніше Retry-After', () => {
  it('перший PATCH 503 з Retry-After 8 — повтор без участі людини через 8 с; тосту немає, правка не утримана', async () => {
    let patches = 0;
    const fetchMock = vi.fn((_path: string, init?: RequestInit) => {
      if (init?.method !== 'PATCH') return Promise.resolve(new Response('{}'));
      patches += 1;

      return Promise.resolve(patches === 1 ? databaseBusy('8') : ok());
    });
    vi.stubGlobal('fetch', fetchMock);
    renderHook(() => useDocumentPending(1), { wrapper });

    putPendingEdit(Table, Period, edit);
    scheduleAutosave();
    await advance(500);

    expect(patchCalls(fetchMock)).toHaveLength(1);
    // ⛔ Мутація: прибрати гілку `ECR-SYS-0503` в `isTransientBusy` — повтору немає (лише `noteSaveFailed`).
    expect(isBusyRetryWaiting()).toBe(true);
    expect(pendingRejections(Table, Period).size).toBe(0);
    expect(sendableEdits(Table, Period)).toEqual([edit]);
    expect(showApiError).not.toHaveBeenCalled();

    // Власний відступ — 2,5 с, але сервер назвав 8 с: раніше повтору немає.
    // ⛔ Мутація: повернути `status !== 429` у `retryAfterOf` — повтор уже на 2,5 с.
    await advance(7_400);
    expect(patchCalls(fetchMock)).toHaveLength(1);

    await advance(100);
    expect(patchCalls(fetchMock)).toHaveLength(2);
    expect(hasPending()).toBe(false);
    expect(isBusyRetryWaiting()).toBe(false);
  });

  it('503 іншого джерела (без ключа databaseBusy) — не минуще «зайнято»: повтор не планується', async () => {
    const fetchMock = vi.fn((_path: string, init?: RequestInit) => {
      if (init?.method !== 'PATCH') return Promise.resolve(new Response('{}'));

      return Promise.resolve(
        new Response(
          JSON.stringify({ title: 'Source unavailable', status: 503, errorCode: 'ECR-INT-0503', correlationId: 'c3' }),
          { status: 503, headers: { 'Content-Type': 'application/problem+json', 'Retry-After': '1' } },
        ),
      );
    });
    vi.stubGlobal('fetch', fetchMock);
    renderHook(() => useDocumentPending(1), { wrapper });

    putPendingEdit(Table, Period, edit);
    scheduleAutosave();
    await advance(500);

    expect(patchCalls(fetchMock)).toHaveLength(1);
    expect(isBusyRetryWaiting()).toBe(false);
    // Правка лишається придатною до надсилання (5xx минущий, `saveErrors.ts`), але без автоповтору.
    expect(sendableEdits(Table, Period)).toEqual([edit]);
  });
});
