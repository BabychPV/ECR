import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { JobsPage } from '@/pages/admin/JobsPage';

/**
 * RC19: фільтр «Types» у Jobs — читабельні назви, за абеткою, а не в порядку появи задач у журналі;
 * тип, якого немає в каталозі, — словами (`ISomeNewJob` → «Some new»), а не іменем класу.
 */
const base = { percent: 0, startedAt: '2026-09-19T09:58:00Z', state: 'Queued', updatedAt: '2026-09-19T10:00:00Z' };
const jobs = [
  { ...base, jobCode: 'Ecr.Application.Ports.ISomeNewJob', jobId: 'ISomeNewJob-1' },
  { ...base, jobCode: 'Ecr.Application.Ports.IRecalculationJob', jobId: 'IRecalculationJob-1' },
  { ...base, jobCode: 'Ecr.Application.Ports.IExcelExportJob', jobId: 'IExcelExportJob-1' },
];

afterEach(() => vi.unstubAllGlobals());

describe('JobsPage: варіанти фільтра «Types»', () => {
  it('за абеткою підписів (у тесті каталог порожній, тож «⟦ключ⟧» передує слову), невідомий тип — словами', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn((input: RequestInfo | URL) =>
        Promise.resolve(
          String(input).includes('/api/v1/jobs')
            ? new Response(JSON.stringify(jobs), { status: 200, headers: { 'Content-Type': 'application/json' } })
            : new Response('null', { status: 404 }),
        ),
      ),
    );
    const user = userEvent.setup();

    render(
      <MantineProvider>
        <MemoryRouter initialEntries={['/admin/jobs']}>
          <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
            <JobsPage />
          </QueryClientProvider>
        </MemoryRouter>
      </MantineProvider>,
    );

    const select = await screen.findByRole('textbox', { name: '⟦jobs.filterType⟧' });
    await waitFor(() => expect(select).toBeTruthy());
    await user.click(select);

    const labels = (await screen.findAllByRole('option')).map((option) => option.textContent);

    expect(labels).toEqual(['⟦jobs.kind.excelExport⟧', '⟦jobs.kind.recalculation⟧', 'Some new']);
  });
});
