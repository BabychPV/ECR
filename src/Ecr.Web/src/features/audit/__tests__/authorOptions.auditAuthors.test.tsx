import type { JSX, ReactNode } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { renderHook, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { useAuthorOptions } from '../authorOptions';

/**
 * ✎ P-1124-4 (живий екран 24.11: «By user» — «Anyone» і виглядає вимкненим): перелік авторів брався лише з
 * `GET /users` за `Security.ManageUsers`, якого аудитор не має; у журналі безпеки рядки «Система» й анонімного входу
 * імені не несуть — варіантів не було жодного. Тепер — `GET /audit/authors` за правом самого журналу
 * (`Security.ViewAudit`), а «Система» й анонімний вхід — теж варіанти.
 *
 * Мутація (локально): прибрати запит `/api/v1/audit/authors` з `useAuthorOptions` — перший тест червоніє (0 варіантів).
 */

vi.mock('@/shared/session/useSession', () => ({
  useSession: () => ({ data: { permissions: ['Security.ViewAudit'] } }),
  can: (session: { permissions: string[] } | undefined, permission: string) => session?.permissions.includes(permission) ?? false,
}));

const urls: string[] = [];

afterEach(() => {
  vi.unstubAllGlobals();
});

function wrapper({ children }: { children: ReactNode }): JSX.Element {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
}

describe('P-1124-4: автори журналу за правом аудиту', () => {
  it('аудитор без ManageUsers бачить усіх авторів з /audit/authors, /users не питається', async () => {
    urls.length = 0;
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL) => {
        const url = String(input);
        urls.push(url);
        const body = url.includes('/api/v1/audit/authors')
          ? [
              { userId: 7, displayName: 'Olena' },
              { userId: 9, displayName: null },
            ]
          : null;

        return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
      }),
    );

    const { result } = renderHook(() => useAuthorOptions([], null), { wrapper });

    await waitFor(() => expect(result.current).toHaveLength(2));
    expect(result.current.find((option) => option.value === '7')?.label).toBe('Olena');
    // Без імені — чесне «користувач #9», а не порожній пункт.
    expect(result.current.find((option) => option.value === '9')?.label).toMatch(/9/u);
    expect(urls).toEqual(['/api/v1/audit/authors']);
  });

  it('AC-7: однакові імена розрізняються логіном з /audit/authors, а не «#id»; унікальне ім’я — без дужок', async () => {
    // Мутація (локально): не брати `login` з `/audit/authors` у `useAuthorOptions` — червоне («Twin (#5)»).
    vi.stubGlobal(
      'fetch',
      vi.fn(async () =>
        new Response(
          JSON.stringify([
            { userId: 5, displayName: 'Twin', login: 'tmp-b12-a' },
            { userId: 6, displayName: 'Twin', login: 'tmp-b12-b' },
            { userId: 7, displayName: 'Olena', login: null },
          ]),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        ),
      ),
    );

    const { result } = renderHook(() => useAuthorOptions([], null), { wrapper });

    await waitFor(() => expect(result.current).toHaveLength(3));
    expect(result.current.find((option) => option.value === '5')?.label).toBe('Twin (tmp-b12-a)');
    expect(result.current.find((option) => option.value === '6')?.label).toBe('Twin (tmp-b12-b)');
    expect(result.current.find((option) => option.value === '7')?.label).toBe('Olena');
  });

  it('«Система» й анонімний вхід із рядків журналу — теж варіанти фільтра', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => new Response('[]', { status: 200, headers: { 'Content-Type': 'application/json' } })),
    );

    const rows = [
      { changedByUserId: 0, changedByDisplayName: null },
      { changedByUserId: -1, changedByDisplayName: null },
    ];
    const { result } = renderHook(() => useAuthorOptions(rows, null), { wrapper });

    await waitFor(() => expect(result.current.map((option) => option.value).sort()).toEqual(['-1', '0']));
  });
});
