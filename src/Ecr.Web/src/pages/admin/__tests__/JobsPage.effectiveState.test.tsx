import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { JobsPage } from '@/pages/admin/JobsPage';

/**
 * Дайджест сповіщень без жодної відправки — `Succeeded` за `state`, але
 * `SucceededWithErrors` за `effectiveState` (сервер: `JobCompletionWarning`).
 * Бейдж у переліку мусить читати `effectiveState`, а не `state`.
 */
const jobs = [
  {
    jobCode: 'Ecr.Infrastructure.Jobs.NotificationJob',
    jobId: 'NotificationJob#1',
    percent: 100,
    startedAt: '2026-09-19T09:58:00Z',
    state: 'Succeeded',
    effectiveState: 'SucceededWithErrors',
    updatedAt: '2026-09-19T10:00:00Z',
  },
  {
    jobCode: 'Ecr.Infrastructure.Jobs.NotificationJob',
    jobId: 'NotificationJob#2',
    percent: 100,
    startedAt: '2026-09-19T09:58:00Z',
    state: 'Succeeded',
    updatedAt: '2026-09-19T10:00:00Z',
  },
];

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('JobsPage: попередження дайджесту', () => {
  it('Succeeded із effectiveState дає warning, без нього — нейтральний Succeeded', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL) =>
        String(input).includes('/api/v1/jobs')
          ? new Response(JSON.stringify(jobs), { status: 200, headers: { 'Content-Type': 'application/json' } })
          : new Response(JSON.stringify(null), { status: 404 }),
      ),
    );
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

    render(
      <MantineProvider>
        <MemoryRouter initialEntries={['/admin/jobs']}>
          <QueryClientProvider client={client}>
            <JobsPage />
          </QueryClientProvider>
        </MemoryRouter>
      </MantineProvider>,
    );

    await waitFor(() => expect(document.querySelectorAll('[data-status-state]')).toHaveLength(2));

    const warn = document.querySelector('[data-status-state="SucceededWithErrors"]');
    const ok = document.querySelector('[data-status-state="Succeeded"]');

    expect(warn?.getAttribute('data-status-tone')).toBe('warning');
    expect(ok?.getAttribute('data-status-tone')).toBe('neutral');
  });
});
