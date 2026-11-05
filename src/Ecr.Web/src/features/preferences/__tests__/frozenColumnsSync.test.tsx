import { beforeEach, describe, expect, it, vi } from 'vitest';
import { act, renderHook } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { JSX, ReactNode } from 'react';
import { deletePreference, getPreferences, putPreference, type UserPreference } from '../api';
import {
  frozenColumnsCacheKey,
  frozenColumnsKey,
  sanitizeFrozenCount,
  serverFrozenColumns,
  useFrozenColumns,
} from '../frozenColumnsSync';
import { PreferencesQueryKey } from '../usePreferenceSync';

/**
 * Закріплені колонки: вибір користувача на ВИЗНАЧЕННЯ таблиці - ключ, кеш першого рендера,
 * пріоритет сервера, запис і видалення («Default» = немає ключа, `0` = свідоме «не закріплювати»).
 */

vi.mock('@/features/preferences/api', () => ({
  getPreferences: vi.fn(),
  putPreference: vi.fn(),
  deletePreference: vi.fn(),
}));

const put = vi.mocked(putPreference);
const remove = vi.mocked(deletePreference);
const get = vi.mocked(getPreferences);

function pref(key: string, value: unknown): UserPreference {
  return { key, value, updatedAt: '2026-11-05T00:00:00Z' } as UserPreference;
}

function makeClient(server?: UserPreference[]): QueryClient {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  if (server !== undefined) client.setQueryData(PreferencesQueryKey, server);
  return client;
}

function wrapperFor(client: QueryClient) {
  return function Wrapper({ children }: { children: ReactNode }): JSX.Element {
    return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
  };
}

beforeEach(() => {
  localStorage.clear();
  put.mockReset().mockImplementation(async (key, value) => pref(key, value));
  remove.mockReset().mockResolvedValue(undefined);
  get.mockReset().mockImplementation(() => new Promise<UserPreference[]>(() => undefined));
});

describe('ключ і розбір значення', () => {
  it('ключ - grid.frozenColumns.{tableDefId}; значення - ціле число', () => {
    expect(frozenColumnsKey(42)).toBe('grid.frozenColumns.42');

    const server = [pref('grid.frozenColumns.42', 3), pref('grid.frozenColumns.7', 0)];
    expect(serverFrozenColumns(server, 42)).toBe(3);
    expect(serverFrozenColumns(server, 7)).toBe(0);
    expect(serverFrozenColumns(server, 8)).toBeUndefined();
  });

  it('сміття (рядок, дріб, від\'ємне, завелике) - не вибір', () => {
    for (const bad of ['3', 1.5, -1, 100000, null, {}, [2], Number.NaN]) {
      expect(sanitizeFrozenCount(bad)).toBeUndefined();
    }
    expect(sanitizeFrozenCount(0)).toBe(0);
    expect(sanitizeFrozenCount(7)).toBe(7);
  });
});

describe('useFrozenColumns', () => {
  it('сервер перемагає кеш; серверне стає кешем наступного першого рендера', () => {
    localStorage.setItem(frozenColumnsCacheKey(9), '2');
    const client = makeClient([pref('grid.frozenColumns.9', 4)]);

    const { result } = renderHook(() => useFrozenColumns(9), { wrapper: wrapperFor(client) });

    expect(result.current.count).toBe(4);
    expect(localStorage.getItem(frozenColumnsCacheKey(9))).toBe('4');
  });

  it('поки сервер не відповів, перший рендер бере кеш', () => {
    localStorage.setItem(frozenColumnsCacheKey(9), '2');

    const { result } = renderHook(() => useFrozenColumns(9), { wrapper: wrapperFor(makeClient()) });

    expect(result.current.count).toBe(2);
  });

  it('сервер відповів без ключа - діє типове правило, застарілий кеш прибирається', () => {
    localStorage.setItem(frozenColumnsCacheKey(9), '2');
    const client = makeClient([]);

    const { result } = renderHook(() => useFrozenColumns(9), { wrapper: wrapperFor(client) });

    expect(result.current.count).toBeUndefined();
    expect(localStorage.getItem(frozenColumnsCacheKey(9))).toBeNull();
  });

  it('вибір пишеться в кеш і одним PUT; 0 - це значення, не відсутність ключа', () => {
    const client = makeClient([]);
    const { result } = renderHook(() => useFrozenColumns(9), { wrapper: wrapperFor(client) });

    act(() => result.current.setCount(0));

    expect(result.current.count).toBe(0);
    expect(localStorage.getItem(frozenColumnsCacheKey(9))).toBe('0');
    expect(put).toHaveBeenCalledTimes(1);
    expect(put).toHaveBeenCalledWith('grid.frozenColumns.9', 0);
    expect(remove).not.toHaveBeenCalled();

    const cached = client.getQueryData<UserPreference[]>(PreferencesQueryKey);
    expect(serverFrozenColumns(cached ?? [], 9)).toBe(0);
  });

  it('«Default» (undefined) видаляє ключ на сервері й у кеші', () => {
    const client = makeClient([pref('grid.frozenColumns.9', 3)]);
    const { result } = renderHook(() => useFrozenColumns(9), { wrapper: wrapperFor(client) });

    act(() => result.current.setCount(undefined));

    expect(result.current.count).toBeUndefined();
    expect(localStorage.getItem(frozenColumnsCacheKey(9))).toBeNull();
    expect(remove).toHaveBeenCalledWith('grid.frozenColumns.9');
    expect(put).not.toHaveBeenCalled();
  });

  it('те саме значення, яке сервер уже має, вдруге не пишеться', () => {
    const client = makeClient([pref('grid.frozenColumns.9', 3)]);
    const { result } = renderHook(() => useFrozenColumns(9), { wrapper: wrapperFor(client) });

    act(() => result.current.setCount(3));

    expect(put).not.toHaveBeenCalled();
  });

  it('локальний вибір не перетирається відповіддю сервера (touched)', () => {
    const client = makeClient([pref('grid.frozenColumns.9', 3)]);
    const { result } = renderHook(() => useFrozenColumns(9), { wrapper: wrapperFor(client) });

    act(() => result.current.setCount(5));
    act(() => {
      client.setQueryData(PreferencesQueryKey, [pref('grid.frozenColumns.9', 1)]);
    });

    expect(result.current.count).toBe(5);
  });

  it('інша таблиця - свій стан', () => {
    localStorage.setItem(frozenColumnsCacheKey(1), '2');
    localStorage.setItem(frozenColumnsCacheKey(2), '5');

    const { result, rerender } = renderHook(({ id }) => useFrozenColumns(id), {
      wrapper: wrapperFor(makeClient()),
      initialProps: { id: 1 },
    });
    expect(result.current.count).toBe(2);

    rerender({ id: 2 });
    expect(result.current.count).toBe(5);
  });

  it('localStorage недоступний - не падає, працює через сервер', () => {
    const spy = vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new Error('blocked');
    });
    const setSpy = vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('blocked');
    });

    try {
      const { result } = renderHook(() => useFrozenColumns(9), { wrapper: wrapperFor(makeClient([])) });
      expect(result.current.count).toBeUndefined();

      act(() => result.current.setCount(2));
      expect(result.current.count).toBe(2);
      expect(put).toHaveBeenCalledWith('grid.frozenColumns.9', 2);
    } finally {
      spy.mockRestore();
      setSpy.mockRestore();
    }
  });
});
