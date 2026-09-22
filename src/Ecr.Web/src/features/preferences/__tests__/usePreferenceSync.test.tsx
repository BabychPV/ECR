import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, renderHook, waitFor } from '@testing-library/react';
import { MantineProvider, useMantineColorScheme } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { JSX, ReactNode } from 'react';
import { usePreferenceSync } from '../usePreferenceSync';
import { setLanguage } from '@/shared/i18n';
import { density, setDensity } from '@/shared/theme/preferences';

/**
 * Синхронізація налаштувань із сервером (`BE-20`).
 *
 * ⚠ Мережа — заглушка `fetch`, що записує КОЖЕН запит: доводи тут — це
 * кількість і вміст запитів, а не «не впало».
 */

interface Call {
  method: string;
  url: string;
  body: unknown;
}

let calls: Call[] = [];
let server: { key: string; value: unknown; updatedAt: string }[] = [];
let getStatus = 200;

function preferenceCalls(method?: string): Call[] {
  return calls.filter(
    (call) => call.url.includes('/api/v1/me/preferences') && (method === undefined || call.method === method),
  );
}

beforeEach(() => {
  calls = [];
  server = [];
  getStatus = 200;
  localStorage.clear();

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const method = init?.method ?? 'GET';
      const body = typeof init?.body === 'string' ? (JSON.parse(init.body) as unknown) : undefined;
      calls.push({ method, url, body });

      if (url === '/api/v1/me/preferences' && method === 'GET') {
        return new Response(JSON.stringify(getStatus === 200 ? server : { title: 'boom' }), {
          status: getStatus,
          headers: { 'Content-Type': 'application/json' },
        });
      }

      if (url.startsWith('/api/v1/me/preferences/') && method === 'PUT') {
        const key = decodeURIComponent(url.slice('/api/v1/me/preferences/'.length));
        return new Response(JSON.stringify({ key, value: body, updatedAt: '2026-09-22T00:00:00Z' }), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }

      // Каталог рядків після застосування мови — порожній, але чинний.
      return new Response(JSON.stringify({ languageCode: 'en', revision: 1, strings: {} }), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      });
    }),
  );
});

afterEach(() => {
  vi.unstubAllGlobals();
});

function wrapper({ children }: { children: ReactNode }): JSX.Element {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  return (
    <MantineProvider>
      <QueryClientProvider client={client}>{children}</QueryClientProvider>
    </MantineProvider>
  );
}

/** Хук разом зі схемою Mantine — щоб бачити застосовану тему. */
function useProbe(enabled: boolean): { generation: number; scheme: string } {
  const generation = usePreferenceSync(enabled);
  const { colorScheme } = useMantineColorScheme();
  return { generation, scheme: colorScheme };
}

async function settle(): Promise<void> {
  await act(async () => {
    await new Promise((resolve) => setTimeout(resolve, 20));
  });
}

describe('usePreferenceSync — BE-20', () => {
  it('значення сервера перемагає локальне: density=compact при локальному comfortable', async () => {
    setDensity('comfortable');
    server = [{ key: 'density', value: 'compact', updatedAt: '2026-09-22T00:00:00Z' }];

    const { result } = renderHook(() => useProbe(true), { wrapper });

    await waitFor(() => {
      expect(density()).toBe('compact');
    });
    expect(document.documentElement.dataset['ecrDensity']).toBe('compact');
    // Перемикач із власним станом має перемонтуватися.
    expect(result.current.generation).toBe(1);

    await settle();
    // Відлуння застосування не йде на сервер.
    expect(preferenceCalls('PUT')).toEqual([]);
  });

  it('тема з сервера застосовується', async () => {
    server = [{ key: 'theme', value: 'dark', updatedAt: '2026-09-22T00:00:00Z' }];

    const { result } = renderHook(() => useProbe(true), { wrapper });

    await waitFor(() => {
      expect(result.current.scheme).toBe('dark');
    });
    await settle();
    expect(preferenceCalls('PUT')).toEqual([]);
  });

  it('зміна мови → рівно один PUT language, і не на кожен рендер', async () => {
    const { rerender } = renderHook(() => useProbe(true), { wrapper });
    await waitFor(() => {
      expect(preferenceCalls('GET')).toHaveLength(1);
    });
    await settle();

    act(() => {
      setLanguage('kz');
    });
    await settle();

    rerender();
    rerender();
    rerender();
    await settle();

    const puts = preferenceCalls('PUT');
    expect(puts).toHaveLength(1);
    expect(puts[0]?.url).toBe('/api/v1/me/preferences/language');
    expect(puts[0]?.body).toBe('kz');

    // Той самий вибір удруге — не новий запис.
    act(() => {
      setLanguage('kz');
    });
    await settle();
    expect(preferenceCalls('PUT')).toHaveLength(1);
    expect(preferenceCalls('GET')).toHaveLength(1);
  });

  it('відмова GET → працює з localStorage, без винятку й без записів', async () => {
    setDensity('comfortable');
    getStatus = 500;

    const { result } = renderHook(() => useProbe(true), { wrapper });

    await waitFor(() => {
      expect(preferenceCalls('GET')).toHaveLength(1);
    });
    await settle();

    expect(density()).toBe('comfortable');
    expect(result.current.generation).toBe(0);
    expect(preferenceCalls('PUT')).toEqual([]);
  });

  it('відповідь GET не масивом → без винятку, локальні значення й жодних записів', async () => {
    setDensity('comfortable');
    server = { unexpected: true } as unknown as typeof server;

    renderHook(() => useProbe(true), { wrapper });

    await waitFor(() => {
      expect(preferenceCalls('GET')).toHaveLength(1);
    });
    await settle();

    expect(density()).toBe('comfortable');
    expect(preferenceCalls('PUT')).toEqual([]);
  });

  it('локальне значення без серверного → одноразовий PUT (міграція)', async () => {
    setDensity('comfortable');

    const { rerender } = renderHook(() => useProbe(true), { wrapper });

    await waitFor(() => {
      expect(preferenceCalls('PUT')).toHaveLength(1);
    });
    rerender();
    rerender();
    await settle();

    const puts = preferenceCalls('PUT');
    expect(puts).toHaveLength(1);
    expect(puts[0]?.url).toBe('/api/v1/me/preferences/density');
    expect(puts[0]?.body).toBe('comfortable');
    expect(density()).toBe('comfortable');
  });

  it('анонім (сторінка входу): жодного запиту, навіть при зміні вибору', async () => {
    setDensity('comfortable');

    renderHook(() => useProbe(false), { wrapper });
    await settle();

    act(() => {
      setLanguage('ru');
      setDensity('compact');
    });
    await settle();

    expect(preferenceCalls()).toEqual([]);
  });
});
