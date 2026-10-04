import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, configure, fireEvent, render, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { Notifications, notifications } from '@mantine/notifications';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { SourceEventsTable } from '@/features/sources/SourceEventsTable';
import { testTheme } from '@/test/render';

/**
 * L9-20: «Отримати з PI зараз» повертає `202` — задача лише в черзі. Перелік
 * подій перечитується, коли задача ЗАВЕРШИЛАСЬ (`Succeeded`), а не на `202`:
 * інакше таблиця перечитувалась до запису синку й лишалась старою.
 */
configure({ asyncUtilTimeout: 10_000 });

const Events = '/api/v1/sources/42/source-events';

let eventReads = 0;
let jobState = 'Running';

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function serve(): void {
  eventReads = 0;
  jobState = 'Running';
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const path = new URL(String(input), 'http://localhost').pathname;
      const method = init?.method ?? 'GET';

      if (path === Events && method === 'GET') {
        eventReads++;
        return json({ items: [], nextCursor: null, totalCount: 0 });
      }
      if (path === `${Events}/sync` && method === 'POST') return json({ jobId: 'sync-1' }, 202);
      if (path === '/api/v1/jobs/sync-1') return json({ jobId: 'sync-1', state: jobState });

      return json(null);
    }),
  );
}

afterEach(() => {
  cleanup();
  notifications.clean();
  vi.unstubAllGlobals();
});

describe('SourceEventsTable — синк зараз', () => {
  it('перелік перечитується на Succeeded задачі, а не на 202', async () => {
    serve();
    render(
      <MantineProvider theme={testTheme}>
        <Notifications />
        <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
          <MemoryRouter>
            <SourceEventsTable sourceEntityId={42} maps={[]} documents={[]} canManage onCreateMap={() => undefined} />
          </MemoryRouter>
        </QueryClientProvider>
      </MantineProvider>,
    );

    await waitFor(() => expect(eventReads).toBe(1));
    fireEvent.click(document.querySelector('[data-source-events-sync]')!);

    // Задача ще йде: опитування є, перечитування переліку — ні.
    await waitFor(() =>
      expect(vi.mocked(fetch).mock.calls.some(([input]) => String(input).includes('/api/v1/jobs/sync-1'))).toBe(true),
    );
    expect(eventReads).toBe(1);

    jobState = 'Succeeded';
    await waitFor(() => expect(eventReads).toBe(2));
  });
});
