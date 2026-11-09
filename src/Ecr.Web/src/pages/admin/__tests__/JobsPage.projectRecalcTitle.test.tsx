import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { JobsPage } from '@/pages/admin/JobsPage';
import { testTheme } from '@/test/render';

/**
 * ✎ a5 (приймання 08.11, P3): у переліку задач батько перерахунку проєкту називався «Recalculation» — так само, як
 * його документні діти; «Project recalculation <CODE>» було лише в «Моїх задачах».
 *
 * ⛔ Доказ: кнопка рядка батька — `recalcProgress.myTaskTitle (project=PRJ-7)`, дитини — назва виду задачі. Мутація
 * «повернути `jobKindLabel(job.jobCode)` у кнопку рядка» — перше твердження червоне.
 */

const Recalc = 'Ecr.Application.Ports.IRecalculationJob';

const rows = [
  {
    jobId: 'IRecalculationJob~project7-year~1',
    jobCode: Recalc,
    state: 'Succeeded',
    percent: 100,
    startedAt: '2026-11-08T09:00:00Z',
    updatedAt: '2026-11-08T09:05:00Z',
    documentId: null,
    projectId: 7,
    projectCode: 'PRJ-7',
    effectiveState: 'Succeeded',
    fanOut: { total: 1, queued: 0, running: 0, succeeded: 1, failed: 0, skippedSheets: 0 },
  },
  {
    jobId: 'IRecalculationJob~doc51-year~1',
    jobCode: Recalc,
    state: 'Succeeded',
    percent: 100,
    startedAt: '2026-11-08T09:01:00Z',
    updatedAt: '2026-11-08T09:04:00Z',
    documentId: 51,
    projectId: 7,
    projectCode: 'PRJ-7',
    fanOutParentJobId: 'IRecalculationJob~project7-year~1',
  },
];

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('JobsPage: назва рядка перерахунку проєкту (a5)', () => {
  it('батько — «Project recalculation <CODE>», дитина — вид задачі', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL) => {
        const url = String(input);
        const body = url.includes('/api/v1/jobs') ? rows : null;
        return new Response(JSON.stringify(body), {
          status: body === null ? 404 : 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }),
    );

    render(
      <MantineProvider theme={testTheme}>
        <MemoryRouter initialEntries={['/admin/jobs']}>
          <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
            <JobsPage />
          </QueryClientProvider>
        </MemoryRouter>
      </MantineProvider>,
    );

    const parent = await screen.findByText('⟦recalcProgress.myTaskTitle (project=PRJ-7)⟧', {}, { timeout: 60_000 });
    expect(parent.closest('[data-job-open]')?.getAttribute('data-job-open')).toBe('IRecalculationJob~project7-year~1');

    const child = document.querySelector('[data-job-open="IRecalculationJob~doc51-year~1"]');
    expect(child?.textContent).not.toContain('recalcProgress.myTaskTitle');
  }, 120_000);
});
