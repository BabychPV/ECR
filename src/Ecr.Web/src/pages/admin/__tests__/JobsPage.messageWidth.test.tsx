import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { JobsPage } from '@/pages/admin/JobsPage';

/**
 * `X-22`: при 1280 перелік задач був ширший за екран (живцем: таблиця 1114 px
 * у вікні 996), і «Started»/«Watch» стояли за правим краєм. Найширше —
 * повідомлення задачі (ключ експорту на 32 знаки без пробілів).
 */

const Key = '02df27c667a74410b463b142ae6bedbf';

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('JobsPage: довге повідомлення не розсуває таблицю', () => {
  it('повідомлення обрізається з повним текстом у title, дія «Watch» — компактна', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () =>
        new Response(
          JSON.stringify([
            { jobCode: 'Ecr.Application.Ports.IExcelExportJob', jobId: 'job-1', percent: 100, state: 'Succeeded', message: Key, startedAt: '2026-09-19T09:00:00Z', updatedAt: '2026-09-19T09:10:00Z' },
          ]),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        ),
      ),
    );

    render(
      <MantineProvider>
        <MemoryRouter initialEntries={['/admin/jobs']}>
          <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
            <JobsPage />
          </QueryClientProvider>
        </MemoryRouter>
      </MantineProvider>,
    );

    const message = await screen.findByText(Key);

    // ⛔ Мутація «повернути голий `job.message`» — ні обрізання, ні `title`.
    expect(message.classList.contains('ecr-ellipsis')).toBe(true);
    expect(message.getAttribute('title')).toBe(Key);
    expect(screen.getByRole('button', { name: '⟦jobs.recentWatch⟧' }).getAttribute('data-size')).toBe('compact-xs');
  });
});
