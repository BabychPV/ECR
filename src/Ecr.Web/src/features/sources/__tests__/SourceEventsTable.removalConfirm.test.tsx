import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, configure, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { Notifications, notifications } from '@mantine/notifications';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { SourceEventsTable } from '@/features/sources/SourceEventsTable';
import { testTheme } from '@/test/render';

/**
 * D-464: прогін синку не видалив зниклі з PI події понад ліміт (`eventRemovalBlocked` завершеної задачі) — людина
 * бачить скільки і яка межа, а «Підтвердити видалення» після діалогу ставить синк із `?confirmRemoval=true`.
 * Без `Integration.Manage` (`canManage = false`) дії немає.
 */
configure({ asyncUtilTimeout: 10_000 });

const Events = '/api/v1/sources/42/source-events';

const posts: string[] = [];

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function serve(): void {
  posts.length = 0;
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = new URL(String(input), 'http://localhost');
      const method = init?.method ?? 'GET';

      if (url.pathname === Events && method === 'GET') return json({ items: [], nextCursor: null, totalCount: 0 });
      if (url.pathname === `${Events}/sync` && method === 'POST') {
        const body = typeof init?.body === 'string' ? init.body : '';
        posts.push(body);
        return json({ jobId: body.includes('confirmRemovals') ? 'sync-2' : 'sync-1' }, 202);
      }
      if (url.pathname === '/api/v1/jobs/sync-1') {
        return json({
          jobId: 'sync-1',
          state: 'Succeeded',
          eventRemovalBlocked: {
            maps: 1,
            candidates: 57,
            limit: 30,
            linked: 150,
            perMap: [{ mapId: 7, candidates: 57 }],
          },
        });
      }
      if (url.pathname === '/api/v1/jobs/sync-2') return json({ jobId: 'sync-2', state: 'Running' });

      return json(null);
    }),
  );
}

function renderTable(canManage: boolean): void {
  render(
    <MantineProvider theme={testTheme}>
      <Notifications />
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <MemoryRouter>
          <SourceEventsTable
            sourceEntityId={42}
            maps={[]}
            documents={[]}
            canManage={canManage}
            onCreateMap={() => undefined}
          />
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  cleanup();
  notifications.clean();
  vi.unstubAllGlobals();
});

describe('SourceEventsTable — ліміт масового видалення (D-464)', () => {
  it('показує лічильники і після підтвердження ставить синк із показаними блоками', async () => {
    serve();
    renderTable(true);

    fireEvent.click(document.querySelector('[data-source-events-sync]')!);

    const banner = await screen.findByTestId('source-events-removal-blocked');
    expect(banner.textContent).toContain('57');
    expect(banner.textContent).toContain('30');
    expect(banner.textContent).toContain('150');
    expect(posts).toEqual(['']);

    fireEvent.click(screen.getByRole('button', { name: /removalConfirmAction|Confirm removal/ }));
    const dialog = await screen.findByRole('dialog');
    fireEvent.click(
      Array.from(dialog.querySelectorAll('button')).find((b) =>
        /removalConfirmVerb|Remove missing events/.test(b.textContent ?? ''),
      )!,
    );

    // ✎ Аудит 31.10 (G3): тіло несе рівно показане — мапінг 7, 57 кандидатів.
    await waitFor(() =>
      expect(posts).toEqual(['', JSON.stringify({ confirmRemovals: [{ mapId: 7, candidates: 57 }] })]),
    );
    await waitFor(() => expect(screen.queryByTestId('source-events-removal-blocked')).toBeNull());
  });
});
