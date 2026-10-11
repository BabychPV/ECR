import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  EcrApiError,
  abandonSwitchedSession,
  apiFetch,
  isSessionClosed,
  onBeforeLoginRedirect,
  SESSION_USER_HEADER,
  resetSignOutForTests,
  setReloadPageForTests,
  setSessionUserId,
  verifySessionOwner,
} from '@/api/client';
import type { CurrentUserDto } from '@/api/types';
import { sendPatchBeacon } from '@/features/grid/useCellPatch';
import {
  SessionChangedStorageKey,
  announceSessionChange,
  listenSessionChange,
  resetSessionChannelForTests,
} from '../sessionChannel';
import { QueryClient } from '@tanstack/react-query';
import { checkSessionUser } from '../useSession';

/**
 * AN-108 / S2-05: зміна користувача в сусідній вкладці помічається, і правки попереднього користувача не їдуть
 * під новим cookie.
 *
 * ⛔ Мутаційні докази: прибери `sessionSwitched` з перевірки в `apiFetchRaw` — падає «мережа закрита»; прибери
 * `isSessionClosed()` із `sendPatchBeacon` — падає «маячок не йде»; прибери звірку в `checkSessionUser` —
 * падає «інший userId у /me».
 */

/** Підробка `BroadcastChannel`: спільна шина між екземплярами, як між вкладками. */
class FakeChannel extends EventTarget {
  static readonly all = new Set<FakeChannel>();

  constructor(readonly name: string) {
    super();
    FakeChannel.all.add(this);
  }

  postMessage(data: unknown): void {
    for (const other of FakeChannel.all) {
      if (other !== this && other.name === this.name) other.dispatchEvent(new MessageEvent('message', { data }));
    }
  }

  close(): void {
    FakeChannel.all.delete(this);
  }
}

const reload = vi.fn();

beforeEach(() => {
  vi.stubGlobal('BroadcastChannel', FakeChannel);
  setReloadPageForTests(reload);
});

afterEach(() => {
  resetSessionChannelForTests();
  FakeChannel.all.clear();
  resetSignOutForTests();
  reload.mockReset();
  vi.unstubAllGlobals();
});

describe('sessionChannel', () => {
  it('сповіщення ІНШОЇ вкладки доходить до слухача', () => {
    const onChange = vi.fn();
    const stop = listenSessionChange(onChange);

    new FakeChannel('ecr.session').postMessage('changed');

    expect(onChange).toHaveBeenCalledTimes(1);
    stop();
  });

  it('власне сповіщення до себе не повертається', () => {
    const onChange = vi.fn();
    const stop = listenSessionChange(onChange);

    announceSessionChange();

    expect(onChange).not.toHaveBeenCalled();
    stop();
  });

  it('запасний транспорт: подія storage з ключем сеансу — так, з іншим ключем — ні', () => {
    const onChange = vi.fn();
    const stop = listenSessionChange(onChange);

    window.dispatchEvent(new StorageEvent('storage', { key: 'інший' }));
    expect(onChange).not.toHaveBeenCalled();

    window.dispatchEvent(new StorageEvent('storage', { key: SessionChangedStorageKey }));
    expect(onChange).toHaveBeenCalledTimes(1);
    stop();
  });
});

describe('abandonSwitchedSession', () => {
  it('лишає слід правок, перезавантажує один раз і закриває мережу', async () => {
    const fetchMock = vi.fn(async () => new Response(null, { status: 204 }));
    vi.stubGlobal('fetch', fetchMock);
    const hook = vi.fn();
    const off = onBeforeLoginRedirect(hook);

    abandonSwitchedSession();
    abandonSwitchedSession();

    expect(hook).toHaveBeenCalledTimes(1);
    expect(reload).toHaveBeenCalledTimes(1);
    expect(isSessionClosed()).toBe(true);

    // Мережа закрита.
    const failure = await apiFetch('/api/v1/documents/5/cells', { method: 'PATCH' }).catch((e: unknown) => e);
    expect(failure).toBeInstanceOf(EcrApiError);
    expect((failure as EcrApiError).problem.status).toBe(401);

    // Маячок `beforeunload` теж не йде.
    sendPatchBeacon(5, { tableInstanceId: 1, periodKey: 202609, cells: [] } as never);

    expect(fetchMock).not.toHaveBeenCalled();
    off();
  });
});

describe('checkSessionUser', () => {
  const me = (userId: number): CurrentUserDto => ({ userId }) as CurrentUserDto;

  it('той самий користувач — сеанс живий; новий кеш (нове завантаження) — з чистого аркуша', () => {
    const client = new QueryClient();
    checkSessionUser(client, me(7));
    checkSessionUser(client, me(7));
    checkSessionUser(new QueryClient(), me(8));

    expect(reload).not.toHaveBeenCalled();
    expect(isSessionClosed()).toBe(false);
  });

  it('інший userId у /me — вкладка покидає сеанс', () => {
    const client = new QueryClient();
    checkSessionUser(client, me(7));
    checkSessionUser(client, me(8));

    expect(reload).toHaveBeenCalledTimes(1);
    expect(isSessionClosed()).toBe(true);
  });
});

/**
 * AN-108 / S2-05, серверний рубіж: небезпечні запити несуть `X-Ecr-User`, а `409 ECR-AUTH-0409` покидає сеанс.
 *
 * ⛔ Мутаційні докази: прибери встановлення заголовка в `apiFetchRaw` або `sessionUserHeaders()` у маячку — падає
 * «заголовок їде»; прибери реакцію на `ECR-AUTH-0409` — падає «409 покидає сеанс».
 */
describe('X-Ecr-User', () => {
  const me = (userId: number): CurrentUserDto => ({ userId }) as CurrentUserDto;

  const headerOf = (call: unknown[] | undefined): string | null => {
    const init = call?.[1] as RequestInit | undefined;
    return new Headers(init?.headers).get(SESSION_USER_HEADER);
  };

  it('заголовок їде на PATCH і в маячку, але не на GET і не до першого /me', async () => {
    const fetchMock = vi.fn(async () => new Response(null, { status: 204 }));
    vi.stubGlobal('fetch', fetchMock);

    await apiFetch('/api/v1/documents/5/cells', { method: 'PATCH' });
    expect(headerOf(fetchMock.mock.calls[0])).toBeNull();

    checkSessionUser(new QueryClient(), me(7));
    await apiFetch('/api/v1/documents/5/cells', { method: 'PATCH' });
    await apiFetch('/api/v1/documents/5');
    sendPatchBeacon(5, { tableInstanceId: 1, periodKey: 202609, cells: [] } as never);

    expect(headerOf(fetchMock.mock.calls[1])).toBe('7');
    expect(headerOf(fetchMock.mock.calls[2])).toBeNull();
    expect(headerOf(fetchMock.mock.calls[3])).toBe('7');
  });

  it('409 ECR-AUTH-0409 — вкладка покидає сеанс, як після сповіщення сусідньої', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () =>
        new Response(JSON.stringify({ status: 409, errorCode: 'ECR-AUTH-0409', title: 'x' }), {
          status: 409,
          headers: { 'Content-Type': 'application/problem+json' },
        })),
    );
    const hook = vi.fn();
    const off = onBeforeLoginRedirect(hook);
    checkSessionUser(new QueryClient(), me(7));

    const failure = await apiFetch('/api/v1/documents/5/cells', { method: 'PATCH' }).catch((e: unknown) => e);

    expect(failure).toBeInstanceOf(EcrApiError);
    expect(hook).toHaveBeenCalledTimes(1);
    expect(reload).toHaveBeenCalledTimes(1);
    expect(isSessionClosed()).toBe(true);
    off();
  });

  it('інший 409 (конфлікт комірки) сеансу не чіпає', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () =>
        new Response(JSON.stringify({ status: 409, errorCode: 'ECR-CELL-0409', title: 'x' }), {
          status: 409,
          headers: { 'Content-Type': 'application/problem+json' },
        })),
    );

    await apiFetch('/api/v1/documents/5/cells', { method: 'PATCH' }).catch(() => undefined);

    expect(reload).not.toHaveBeenCalled();
    expect(isSessionClosed()).toBe(false);
  });
});

/**
 * R2-05: повторний вхід ТОГО САМОГО користувача не перезавантажує інші вкладки.
 *
 * ⛔ Мутаційні докази: поверни у `main.tsx` `listenSessionChange(abandonSwitchedSession)` — вкладка
 * перезавантажується при тому самому користувачі (та сама поведінка, що в `verifySessionOwner` без перевірки);
 * прибери порівняння `userId` — падає «інший користувач».
 */
describe('verifySessionOwner', () => {
  const meResponse = (userId: number): Response =>
    new Response(JSON.stringify({ userId }), { status: 200, headers: { 'Content-Type': 'application/json' } });

  it('той самий користувач — вкладка не перезавантажується і правки лишаються', async () => {
    const fetchMock = vi.fn(async () => meResponse(7));
    vi.stubGlobal('fetch', fetchMock);
    const hook = vi.fn();
    const off = onBeforeLoginRedirect(hook);
    setSessionUserId(7);

    await Promise.all([verifySessionOwner(), verifySessionOwner()]);

    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(reload).not.toHaveBeenCalled();
    expect(hook).not.toHaveBeenCalled();
    expect(isSessionClosed()).toBe(false);
    off();
  });

  it('інший користувач — слід правок лишається, вкладка перезавантажується', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => meResponse(8)));
    const hook = vi.fn();
    const off = onBeforeLoginRedirect(hook);
    setSessionUserId(7);

    await verifySessionOwner();

    expect(hook).toHaveBeenCalledTimes(1);
    expect(reload).toHaveBeenCalledTimes(1);
    expect(isSessionClosed()).toBe(true);
    off();
  });

  it('401 (вихід в іншій вкладці) і збій мережі — вкладка покидає сеанс', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => new Response(null, { status: 401 })));
    setSessionUserId(7);
    await verifySessionOwner();
    expect(reload).toHaveBeenCalledTimes(1);

    resetSignOutForTests();
    reload.mockReset();
    vi.stubGlobal('fetch', vi.fn(async () => Promise.reject(new TypeError('offline'))));
    setSessionUserId(7);
    await verifySessionOwner();
    expect(reload).toHaveBeenCalledTimes(1);
  });

  it('вкладка ще не бачила /me — власник невідомий, безпечніше покинути без запиту', async () => {
    const fetchMock = vi.fn(async () => meResponse(7));
    vi.stubGlobal('fetch', fetchMock);

    await verifySessionOwner();

    expect(fetchMock).not.toHaveBeenCalled();
    expect(reload).toHaveBeenCalledTimes(1);
  });
});
