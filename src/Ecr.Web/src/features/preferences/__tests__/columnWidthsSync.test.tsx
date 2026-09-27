import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, renderHook } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { JSX, ReactNode } from 'react';
import { deletePreference, getPreferences, putPreference, type UserPreference } from '../api';
import {
  ColumnWidthsDebounceMs,
  ColumnWidthsWriter,
  columnWidthsCacheKey,
  columnWidthsKey,
  serverColumnWidths,
  useColumnWidths,
} from '../columnWidthsSync';
import { PreferencesQueryKey } from '../usePreferenceSync';
import { DefaultColumnWidth } from '@/features/grid/columnWidths';

/**
 * Ширини колонок на користувача й визначення таблиці (`ФВ-14.29`, `D-201`).
 *
 * ⚠ Мережа — мок `api.ts`: доводи тут — кількість і вміст викликів
 * `putPreference`/`deletePreference`, а не «не впало».
 */

vi.mock('../api', () => ({
  getPreferences: vi.fn(),
  putPreference: vi.fn(),
  deletePreference: vi.fn(),
}));

const put = vi.mocked(putPreference);
const remove = vi.mocked(deletePreference);
const get = vi.mocked(getPreferences);

function pref(key: string, value: unknown): UserPreference {
  return { key, value, updatedAt: '2026-09-28T00:00:00Z' } as UserPreference;
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
  vi.useFakeTimers();
  localStorage.clear();
  put.mockReset().mockImplementation(async (key, value) => pref(key, value));
  remove.mockReset().mockResolvedValue(undefined);
  // Відповідь GET не приходить, доки тест не засіяв кеш сам.
  get.mockReset().mockImplementation(() => new Promise<UserPreference[]>(() => undefined));
});

afterEach(() => {
  vi.useRealTimers();
});

describe('ФВ-14.29: ключ налаштування', () => {
  it('ФВ-14.29: ключ — grid.columnWidths.{tableDefId}, а не екземпляр таблиці', () => {
    expect(columnWidthsKey(42)).toBe('grid.columnWidths.42');

    const server = [pref('grid.columnWidths.42', { NAME: 300 }), pref('grid.columnWidths.7', { NAME: 90 })];
    expect(serverColumnWidths(server, 42)).toEqual({ NAME: 300 });
    expect(serverColumnWidths(server, 43)).toBeUndefined();
  });

  it('ФВ-14.29: сміття з сервера не стає шириною', () => {
    const server = [pref('grid.columnWidths.1', { A: 'wide', B: -5, C: Number.NaN, D: 210 })];
    expect(serverColumnWidths(server, 1)).toEqual({ D: 210 });
  });
});

describe('ФВ-14.29: відкладений запис на сервер', () => {
  function writer(server: Record<string, number> | undefined | null = undefined) {
    let known = server;
    const written: (Record<string, number> | undefined)[] = [];
    const instance = new ColumnWidthsWriter(5, {
      readServer: () => known,
      writeServer: (value) => {
        known = value;
        written.push(value);
      },
    });
    return { instance, written };
  }

  it('ФВ-14.29: серія змін за дебаунс дає ОДИН PUT з останніми ширинами', () => {
    const { instance } = writer();

    instance.change({ NAME: 200 });
    vi.advanceTimersByTime(300);
    instance.change({ NAME: 250 });
    vi.advanceTimersByTime(300);
    instance.change({ CODE: 80 });

    expect(put).not.toHaveBeenCalled();
    vi.advanceTimersByTime(ColumnWidthsDebounceMs);

    expect(put).toHaveBeenCalledTimes(1);
    expect(put).toHaveBeenCalledWith('grid.columnWidths.5', { NAME: 250, CODE: 80 });
  });

  it('ФВ-14.29: перед PUT зміни зливаються з останнім серверним значенням', () => {
    const { instance } = writer({ NAME: 320, UNIT: 60 });

    instance.change({ UNIT: 100 });
    vi.advanceTimersByTime(ColumnWidthsDebounceMs);

    expect(put).toHaveBeenCalledWith('grid.columnWidths.5', { NAME: 320, UNIT: 100 });
  });

  it('ФВ-14.29: усі ширини типові — DELETE ключа, а не PUT порожнього', () => {
    const { instance, written } = writer({ NAME: 320 });

    instance.change({ NAME: DefaultColumnWidth });
    vi.advanceTimersByTime(ColumnWidthsDebounceMs);

    expect(put).not.toHaveBeenCalled();
    expect(remove).toHaveBeenCalledTimes(1);
    expect(remove).toHaveBeenCalledWith('grid.columnWidths.5');
    expect(written).toEqual([undefined]);
  });

  it('ФВ-14.29: типові ширини без ключа на сервері — жодного запиту', () => {
    const { instance } = writer(undefined);

    instance.change({ NAME: DefaultColumnWidth });
    vi.advanceTimersByTime(ColumnWidthsDebounceMs);

    expect(put).not.toHaveBeenCalled();
    expect(remove).not.toHaveBeenCalled();
  });

  it('ФВ-14.29: значення, яке сервер уже має, вдруге не пишеться', () => {
    const { instance } = writer({ NAME: 320 });

    instance.change({ NAME: 320 });
    vi.advanceTimersByTime(ColumnWidthsDebounceMs);

    expect(put).not.toHaveBeenCalled();
  });

  it('ФВ-14.29: відмова PUT не кидає і не повторюється сама', async () => {
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => undefined);
    put.mockRejectedValue(new Error('offline'));
    const { instance } = writer();

    expect(() => {
      instance.change({ NAME: 200 });
      vi.advanceTimersByTime(ColumnWidthsDebounceMs);
    }).not.toThrow();
    await vi.advanceTimersByTimeAsync(10 * ColumnWidthsDebounceMs);

    expect(put).toHaveBeenCalledTimes(1);
    // ⛔ Відмова ПІЙМАНА (тихий рядок розробнику), а не втекла необробленою:
    // без `.catch` цей рядок не з'являється.
    expect(warn).toHaveBeenCalledWith('Preference sync failed: grid.columnWidths.5', expect.any(Error));
    warn.mockRestore();

    // Наступна зміна людини — нова спроба.
    instance.change({ NAME: 210 });
    await vi.advanceTimersByTimeAsync(ColumnWidthsDebounceMs);
    expect(put).toHaveBeenCalledTimes(2);
  });
});

describe('ФВ-14.29: хук useColumnWidths', () => {
  it('ФВ-14.29: значення сервера перемагає кеш localStorage', () => {
    localStorage.setItem(columnWidthsCacheKey(9), JSON.stringify({ NAME: 180 }));
    const client = makeClient([pref('grid.columnWidths.9', { NAME: 260 })]);

    const { result } = renderHook(() => useColumnWidths(9), { wrapper: wrapperFor(client) });

    expect(result.current.widths).toEqual({ NAME: 260 });
    // Серверне стає кешем наступного першого рендера.
    expect(JSON.parse(localStorage.getItem(columnWidthsCacheKey(9)) ?? '{}')).toEqual({ NAME: 260 });
  });

  it('ФВ-14.29: поки сервер не відповів, перший рендер бере кеш', () => {
    localStorage.setItem(columnWidthsCacheKey(9), JSON.stringify({ NAME: 180 }));
    const client = makeClient();

    const { result } = renderHook(() => useColumnWidths(9), { wrapper: wrapperFor(client) });

    expect(result.current.widths).toEqual({ NAME: 180 });
  });

  it('ФВ-14.29: зміна пишеться в кеш одразу, на сервер — одним PUT після дебаунсу', () => {
    const client = makeClient([]);
    const { result } = renderHook(() => useColumnWidths(9), { wrapper: wrapperFor(client) });

    act(() => result.current.onResize({ NAME: 200 }));
    act(() => result.current.onResize({ NAME: 240 }));

    expect(result.current.widths).toEqual({ NAME: 240 });
    expect(JSON.parse(localStorage.getItem(columnWidthsCacheKey(9)) ?? '{}')).toEqual({ NAME: 240 });
    expect(put).not.toHaveBeenCalled();

    act(() => {
      vi.advanceTimersByTime(ColumnWidthsDebounceMs);
    });

    expect(put).toHaveBeenCalledTimes(1);
    expect(put).toHaveBeenCalledWith('grid.columnWidths.9', { NAME: 240 });
  });

  it('ФВ-14.29: інший період тієї самої таблиці (інший tableInstanceId, той самий tableDefId) — ті самі ширини', () => {
    const client = makeClient([]);
    // Період 202608: екземпляр 1001 визначення 9.
    const first = renderHook(() => useColumnWidths(9), { wrapper: wrapperFor(client) });
    act(() => first.result.current.onResize({ NAME: 310 }));
    act(() => {
      vi.advanceTimersByTime(ColumnWidthsDebounceMs);
    });
    first.unmount();

    // Період 202609: екземпляр 2002 того самого визначення 9. Кеш
    // localStorage прибрано — ширини мусять прийти з кешу запиту сервера.
    localStorage.clear();
    const second = renderHook(() => useColumnWidths(9), { wrapper: wrapperFor(client) });

    expect(second.result.current.widths).toEqual({ NAME: 310 });
    expect(put).toHaveBeenCalledWith('grid.columnWidths.9', { NAME: 310 });
    expect(put.mock.calls.every(([key]) => key === 'grid.columnWidths.9')).toBe(true);
  });

  it('ФВ-14.29: розмонтування до кінця дебаунсу не губить зміну', () => {
    const client = makeClient([]);
    const { result, unmount } = renderHook(() => useColumnWidths(9), { wrapper: wrapperFor(client) });

    act(() => result.current.onResize({ CODE: 70 }));
    unmount();

    expect(put).toHaveBeenCalledTimes(1);
    expect(put).toHaveBeenCalledWith('grid.columnWidths.9', { CODE: 70 });
  });

  it('ФВ-14.29: повернення всіх ширин до типових знімає ключ на сервері', () => {
    const client = makeClient([pref('grid.columnWidths.9', { NAME: 260 })]);
    const { result } = renderHook(() => useColumnWidths(9), { wrapper: wrapperFor(client) });

    act(() => result.current.onResize({ NAME: DefaultColumnWidth }));
    act(() => {
      vi.advanceTimersByTime(ColumnWidthsDebounceMs);
    });

    expect(result.current.widths).toEqual({});
    expect(remove).toHaveBeenCalledWith('grid.columnWidths.9');
    expect(put).not.toHaveBeenCalled();
    expect(localStorage.getItem(columnWidthsCacheKey(9))).toBeNull();
  });
});
