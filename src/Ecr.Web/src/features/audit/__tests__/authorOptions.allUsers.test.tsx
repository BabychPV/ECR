import type { JSX, ReactNode } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { renderHook, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { useAuthorOptions } from '../authorOptions';

/**
 * PSS-19c: фільтр автора журналу брав `GET /users?limit=200` — 201-й користувач не мав імені й не обирався.
 * Тепер — усі сторінки (`allUsersQuery` → `fetchAllPages`).
 *
 * Мутаційний доказ (не в коміті): `allUsersQuery.queryFn` читає лише першу сторінку → червоний (немає «User 201»).
 */

vi.mock('@/shared/session/useSession', () => ({
  useSession: () => ({ data: { permissions: ['Security.ManageUsers'] } }),
  can: (session: { permissions: string[] } | undefined, permission: string) => session?.permissions.includes(permission) ?? false,
}));

const urls: string[] = [];

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('PSS-19c: автори журналу — усі користувачі', () => {
  it('користувач із другої сторінки переліку є серед варіантів', async () => {
    urls.length = 0;
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL) => {
        const url = String(input);
        urls.push(url);
        const second = url.includes('cursor=');
        const ids = second ? [201] : Array.from({ length: 200 }, (_, i) => i + 1);

        return new Response(
          JSON.stringify({
            items: ids.map((id) => ({ id, userName: `u${String(id)}`, displayName: `User ${String(id)}` })),
            nextCursor: second ? null : 'p2',
            totalCount: 201,
          }),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        );
      }),
    );

    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const wrapper = ({ children }: { children: ReactNode }): JSX.Element => (
      <QueryClientProvider client={client}>{children}</QueryClientProvider>
    );
    const { result } = renderHook(() => useAuthorOptions([], null), { wrapper });

    await waitFor(() => expect(result.current).toHaveLength(201));
    expect(result.current.some((option) => option.value === '201' && option.label === 'User 201')).toBe(true);
    expect(urls).toEqual(['/api/v1/users?limit=500', '/api/v1/users?limit=500&cursor=p2']);
  });
});
