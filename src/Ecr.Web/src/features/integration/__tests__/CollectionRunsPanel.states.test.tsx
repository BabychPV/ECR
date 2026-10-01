import { afterEach, describe, expect, it, vi } from 'vitest';
import { configure, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { CollectionRunsPanel } from '@/features/integration/CollectionRunsPanel';
import { testTheme } from '@/test/render';

/**
 * Журнал прогонів збору: стани «немає права», «порожньо», «завантаження»
 * (`ФВ-14.22`). Відмову 500 уже тримає `CollectionRunsPanel.render.test.tsx`
 * («L10: відмова переліку»), тут — решта трьох.
 */
configure({ asyncUtilTimeout: 10_000 });

function serve(runs: () => Promise<Response>): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const path = new URL(String(input), 'http://localhost').pathname;

      if (path === '/api/v1/collection-runs') return runs();

      // З'єднання й сутності — лише варіанти фільтрів.
      if (path === '/api/v1/data-sources' || path === '/api/v1/sources') {
        return new Response('[]', { status: 200, headers: { 'Content-Type': 'application/json' } });
      }

      return new Response('null', { status: 200, headers: { 'Content-Type': 'application/json' } });
    }),
  );
}

const problem = (status: number, errorCode: string): Promise<Response> =>
  Promise.resolve(
    new Response(
      JSON.stringify({ title: 'Forbidden', status, errorCode, correlationId: 'corr-runs', detail: null }),
      { status, headers: { 'Content-Type': 'application/problem+json' } },
    ),
  );

function show(): void {
  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <MemoryRouter initialEntries={['/admin/sources']}>
          <CollectionRunsPanel />
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('CollectionRunsPanel — стани', () => {
  it('403: стан «немає права», а не «журнал порожній»', async () => {
    serve(() => problem(403, 'ECR-AUTH-0403'));
    show();

    expect((await screen.findByRole('alert')).textContent).toContain('ECR-AUTH-0403');
    expect(screen.queryByText('⟦collectionRuns.empty⟧')).toBeNull();
  });

  it('порожня сторінка: «журнал порожній», жодної таблиці й кнопки «показати ще»', async () => {
    serve(() =>
      Promise.resolve(
        new Response(JSON.stringify({ items: [], nextCursor: null, totalCount: null }), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        }),
      ),
    );
    show();

    expect(await screen.findByText('⟦collectionRuns.empty⟧')).toBeTruthy();
    expect(screen.queryByRole('table')).toBeNull();
    expect(document.querySelector('[data-collection-runs-more]')).toBeNull();
  });

  it('у дорозі: «завантаження», а не «журнал порожній»', async () => {
    serve(() => new Promise<Response>(() => undefined));
    show();

    expect((await screen.findByRole('status')).getAttribute('aria-busy')).toBe('true');
    expect(screen.queryByText('⟦collectionRuns.empty⟧')).toBeNull();
  });
});
