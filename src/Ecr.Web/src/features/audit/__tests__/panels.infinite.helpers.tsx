import { vi } from 'vitest';
import { render } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { JSX } from 'react';
import { testTheme } from '@/test/render';

/**
 * W-3d/e/f: спільна обв'язка тестів нескінченної прокрутки журналів-панелей. Дві сторінки одного ендпоінта:
 * без курсора — `first` і `nextCursor: 'c2'`, з `cursor=c2` — `second`.
 */
export const SlowEnvTimeout = 400_000;

const json = (body: unknown): Response =>
  new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });

export function servePages(path: string, first: unknown, second: unknown): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = new URL(String(input), 'http://x');

      if (url.pathname === '/api/v1/me') return json({ permissions: [], denies: [], grants: {} });
      if (url.pathname === path) {
        return url.searchParams.get('cursor') === null
          ? json({ items: [first], nextCursor: 'c2', totalCount: null })
          : json({ items: [second], nextCursor: null, totalCount: null });
      }

      return json({ items: [], nextCursor: null, totalCount: 0 });
    }),
  );
}

export function showPanel(panel: JSX.Element, entry: string): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter initialEntries={[entry]}>
        <QueryClientProvider client={client}>{panel}</QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}
