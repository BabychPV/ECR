import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  EcrApiError,
  abandonSwitchedSession,
  apiFetch,
  isSessionClosed,
  onBeforeLoginRedirect,
  resetSignOutForTests,
  setReloadPageForTests,
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
