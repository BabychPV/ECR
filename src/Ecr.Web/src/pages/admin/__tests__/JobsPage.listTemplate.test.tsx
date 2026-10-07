import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, fireEvent, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter, useLocation } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { JSX } from 'react';
import { JobsPage } from '@/pages/admin/JobsPage';

/**
 * UI-28: журнал задач на шаблоні переліку (макет `screens-ops.js`
 * `/admin/jobs`, `32-jobs.png`).
 *
 * Стережеться: пояснення під заголовком; смуга running / queued / failed
 * рахує переданий перелік і фільтрує кліком; пошук, тип і стан — в адресі;
 * «лише мої» — в адресі й у запиті; старе посилання `?id=` відкриває шторку;
 * провалена й скасована задача кажуть, на скількох відсотках зупинились.
 */

const jobs = [
  { jobCode: 'Ecr.Application.Ports.IRecalculationJob', jobId: 'run-1', percent: 40, state: 'Running', startedAt: '2026-10-06T09:00:00Z', updatedAt: '2026-10-06T09:01:00Z', createdByDisplayName: 'Olena', documentId: 12 },
  { jobCode: 'Ecr.Application.Ports.IRecalculationJob', jobId: 'run-2', percent: 0, state: 'Queued', startedAt: '2026-10-06T09:02:00Z', updatedAt: '2026-10-06T09:02:00Z', createdByDisplayName: 'Olena' },
  { jobCode: 'Ecr.Application.Ports.IExcelExportJob', jobId: 'exp-1', percent: 35, state: 'Failed', startedAt: '2026-10-06T08:00:00Z', updatedAt: '2026-10-06T08:01:00Z', createdByDisplayName: null, errorCode: 'ECR-JOB-0409' },
  { jobCode: 'Ecr.Application.Ports.IExcelExportJob', jobId: 'exp-2', percent: 100, state: 'Succeeded', startedAt: '2026-10-06T07:00:00Z', updatedAt: '2026-10-06T07:01:00Z', createdByDisplayName: 'Taras', message: 'Export ready' },
];

const requested: string[] = [];

function respond(): void {
  requested.length = 0;
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      requested.push(url);
      const json = (body: unknown, status = 200): Response =>
        new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });

      if (url.includes('/api/v1/jobs/summary')) {
        return json(
          url.includes('mine=true')
            ? { running: 1, queued: 1, failed24h: 0, succeeded24h: 0, avgStartLatencyMs: null }
            : { running: 2, queued: 5, failed24h: 7, succeeded24h: 40, avgStartLatencyMs: 1440 },
        );
      }

      const one = /\/api\/v1\/jobs\/([^/?]+)$/.exec(url);
      if (one !== null) {
        const job = jobs.find((row) => row.jobId === decodeURIComponent(one[1] ?? ''));
        return job === undefined ? json(null, 404) : json({ ...job, error: null, message: job.message ?? null });
      }
      if (url.includes('/api/v1/jobs')) return json(url.includes('mine=true') ? jobs.slice(0, 2) : jobs);

      return json(null, 404);
    }),
  );
}

let location = '';

function LocationProbe(): JSX.Element | null {
  location = useLocation().search;
  return null;
}

function show(entry = '/admin/jobs'): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider>
      <MemoryRouter initialEntries={[entry]}>
        <QueryClientProvider client={client}>
          <JobsPage />
          <LocationProbe />
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

function shownIds(): string[] {
  return Array.from(document.querySelectorAll('tbody [data-job-open]')).map((node) => node.getAttribute('data-job-open') ?? '');
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('JobsPage на шаблоні переліку (UI-28)', () => {
  it('пояснення під заголовком; смуга — лічильники СЕРВЕРА (`GET /jobs/summary`), не перелік', async () => {
    respond();
    show();

    await waitFor(() => expect(shownIds()).toHaveLength(4));
    // Рівно одне пояснення під заголовком — у рядку пояснення, не другим `meta` (batch-2-a, дефект 3).
    expect(screen.getAllByTestId('page-description').map((node) => node.textContent)).toEqual(['⟦jobs.description⟧']);
    expect(screen.getAllByText('⟦jobs.description⟧')).toHaveLength(1);

    const strip = screen.getByRole('group', { name: '⟦jobs.statsLabel⟧' });
    const value = (label: string): string =>
      within(strip).getByText(label).closest('[data-stat]')?.querySelector('[data-stat-value]')?.textContent ?? '';

    // ⛔ Мутаційний доказ: поверніть лічбу по переліку (`all.filter(…)`) —
    // буде 1/1/1 замість 2/5/7: перелік — лише останні задачі, а «провалені
    // за добу» з нього не порахувати.
    expect(value('⟦jobs.statRunning⟧')).toBe('2');
    expect(value('⟦jobs.statQueued⟧')).toBe('5');
    expect(value('⟦jobs.statFailed⟧')).toBe('7');

    // Затримка старту — підказкою «у черзі»: 1440 мс → 1.4 с.
    expect(document.body.innerHTML).toContain('⟦jobs.statLatency (seconds=1.4)⟧');
  });

  it('клац по показнику фільтрує перелік і пише стан в адресу', async () => {
    respond();
    show();

    await waitFor(() => expect(shownIds()).toHaveLength(4));
    fireEvent.click(screen.getByRole('button', { name: /⟦jobs.statFailed⟧/ }));

    await waitFor(() => expect(shownIds()).toEqual(['exp-1']));
    expect(location).toContain('state=Failed');
  });

  it('пошук і тип — з адреси; нічого не підійшло — «no match», а не «задач ще не було»', async () => {
    respond();
    show('/admin/jobs?type=Ecr.Application.Ports.IExcelExportJob&q=taras');

    await waitFor(() => expect(shownIds()).toEqual(['exp-2']));

    respond();
    show('/admin/jobs?q=nobody-here');
    expect(await screen.findByText('⟦jobs.noMatch⟧')).toBeTruthy();
    expect(screen.queryByText('⟦jobs.recentEmpty⟧')).toBeNull();
  });

  it('«лише мої» — в адресі (?mine=1) і в запиті до сервера', async () => {
    respond();
    show('/admin/jobs?mine=1');

    await waitFor(() => expect(shownIds()).toEqual(['run-1', 'run-2']));
    expect(requested.some((url) => url.endsWith('/api/v1/jobs?mine=true'))).toBe(true);
    // ⚠ Смуга — тієї ж межі: без права на всю чергу сервер віддає лише власні.
    expect(requested.some((url) => url.endsWith('/api/v1/jobs/summary?mine=true'))).toBe(true);
    expect((screen.getByRole('checkbox', { name: '⟦jobs.mineOnly⟧' }) as HTMLInputElement).checked).toBe(true);
  });

  it('старе посилання ?id= відкриває шторку тієї самої задачі', async () => {
    respond();
    show('/admin/jobs?id=exp-1');

    // ⛔ Мутаційний доказ: приберіть перекладання `?id=` у `?panel=` — шторки
    // не буде, а посилання з `SourcesPage`/`RegistryImpactPage` і листів
    // відкриватимуть голий перелік.
    await waitFor(() => expect(location).toBe('?panel=exp-1'));
    const drawer = await waitFor(() => {
      const node = document.querySelector('[data-panel="exp-1"]');
      expect(node).not.toBeNull();
      return node as HTMLElement;
    });

    expect(await within(drawer).findByText('⟦jobs.whyFailed⟧')).toBeTruthy();
    expect(within(drawer).getByText('⟦err.ECR-JOB-0409⟧')).toBeTruthy();
  });

  it('провалена задача каже, на скількох відсотках зупинилась; виконувана — смугою', async () => {
    respond();
    show();

    await waitFor(() => expect(shownIds()).toHaveLength(4));
    const failed = document.querySelector('[data-job-open="exp-1"]')?.closest('tr') as HTMLElement;
    expect(within(failed).getByText('⟦jobs.stoppedAt (percent=35)⟧')).toBeTruthy();

    const running = document.querySelector('[data-job-open="run-1"]')?.closest('tr') as HTMLElement;
    expect(within(running).getByRole('progressbar')).toBeTruthy();
  });
});
