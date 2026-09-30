import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, waitFor, within, fireEvent } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { JobsPage } from '@/pages/admin/JobsPage';

/**
 * Перелік `RecentJobs` (`#/admin/jobs`): «Повторити» й посилання на файл
 * результату (`UX-09`, директива №11, T10 #40).
 *
 * ⛔ До цієї правки перелік показував лише «дивитись»/«скасувати» —
 * `JobRetry`/`JobResultLink` уже існували й стояли в шухляді «My tasks»
 * (`MyTasksDrawer.test.tsx`), але не в загальній таблиці задач. Той самий
 * критерій показу, ті самі компоненти `JobFacts`, той самий каталог рядків
 * (`jobs.restart`, `jobs.resultDownload`) — нових ключів це не додає.
 *
 * ⚠ `hasViewHealth` у `JobsPage.tsx` — буквально `true`: маршрут
 * `/admin/jobs` уже вимагає `System.ViewHealth` (`routes.ts`, `RouteGuard`),
 * тобто кожен, хто бачить цей рядок, право має. Тому мутаційний доказ
 * «нема ViewHealth» тут не потрібен (він живе в `jobRetry.test.tsx` для
 * чистої функції `canRestartJob`) — тут доводимо лише видимість за СТАНОМ і
 * коректність `resultUrl`.
 */

const jobs = [
  {
    jobCode: 'Ecr.Application.Ports.IRecalculationJob',
    jobId: 'IRecalculationJob#failed-1',
    percent: 0,
    state: 'Failed',
    errorCode: 'ECR-JOB-0409',
    startedAt: '2026-09-19T10:00:00Z',
    updatedAt: '2026-09-19T10:05:00Z',
  },
  {
    jobCode: 'Ecr.Application.Ports.IExcelExportJob',
    jobId: 'IExcelExportJob-succeeded-1',
    percent: 100,
    state: 'Succeeded',
    resultUrl: '/api/v1/documents/42/export/abc-123',
    startedAt: '2026-09-19T09:00:00Z',
    updatedAt: '2026-09-19T09:10:00Z',
  },
  {
    jobCode: 'Ecr.Application.Ports.IRecalculationJob',
    jobId: 'IRecalculationJob#running-1',
    percent: 40,
    state: 'Running',
    startedAt: '2026-09-19T11:00:00Z',
    updatedAt: '2026-09-19T11:01:00Z',
  },
];

/** Адреси, куди сторінка сходила методом POST. */
const posted: string[] = [];

function mockFetch(): void {
  posted.length = 0;

  vi.stubGlobal(
    'fetch',
    vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);

      if (init?.method === 'POST') posted.push(url);

      if (url.includes('/restart')) {
        return Promise.resolve(
          new Response(JSON.stringify({ jobId: 'IRecalculationJob#failed-1' }), {
            status: 202,
            headers: { 'Content-Type': 'application/json' },
          }),
        );
      }

      if (url.endsWith('/api/v1/jobs')) {
        return Promise.resolve(
          new Response(JSON.stringify(jobs), {
            status: 200,
            headers: { 'Content-Type': 'application/json' },
          }),
        );
      }

      return Promise.resolve(new Response(JSON.stringify(null), { status: 404 }));
    }),
  );
}

function show(): void {
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
}

/** Рядок переліку, впізнаний за значком стану (`data-status-state`, той самий локатор, що в `JobsPage.cancelJob.test.tsx`). */
function rowOfState(state: string): HTMLElement {
  const row = document.querySelector(`[data-status-state="${state}"]`)?.closest('tr') ?? null;
  expect(row, `рядок задачі у стані «${state}»`).not.toBeNull();

  return row as HTMLElement;
}

async function findRowOfState(state: string): Promise<HTMLElement> {
  await waitFor(() =>
    expect(document.querySelector(`[data-status-state="${state}"]`)).not.toBeNull(),
  );

  return rowOfState(state);
}

afterEach(() => {
  vi.unstubAllGlobals();
  posted.length = 0;
});

describe('JobsPage: «Повторити» в переліку останніх задач', () => {
  it('провалена задача — кнопка «Повторити» є й шле POST на /restart', async () => {
    mockFetch();
    show();

    const retryInRow = within(await findRowOfState('Failed')).getByRole('button', {
      name: '⟦jobs.restart⟧',
    });

    fireEvent.click(retryInRow);

    // ⚠ Той самий привід, що в `cancelJob.test.tsx`: `#` у `jobId` починає
    // фрагмент URL і мусить бути закодований.
    await waitFor(() => expect(posted.length).toBe(1));
    expect(posted[0]).toBe('/api/v1/jobs/IRecalculationJob%23failed-1/restart');
    expect(posted[0]).not.toContain('#');
  });

  it('мутація: задача не в термінальному невдалому стані — кнопки «Повторити» немає', async () => {
    mockFetch();
    show();

    await findRowOfState('Failed');

    /*
     * ⛔ Мутаційний доказ: приберіть умову видимості (`canRestartJob` /
     * `state === 'Failed'`) — і кнопка з'явиться в рядку `Running` та
     * `Succeeded`, а цей тест впаде. Сусідній рядок `Failed` доводить, що
     * кнопка взагалі рендериться в цьому переліку.
     */
    expect(
      within(rowOfState('Running')).queryByRole('button', { name: '⟦jobs.restart⟧' }),
    ).toBeNull();
    expect(
      within(rowOfState('Succeeded')).queryByRole('button', { name: '⟦jobs.restart⟧' }),
    ).toBeNull();
    expect(
      within(rowOfState('Failed')).getByRole('button', { name: '⟦jobs.restart⟧' }),
    ).toBeTruthy();
  });
});

describe('JobsPage: посилання на файл результату в переліку останніх задач', () => {
  it('resultUrl не null — посилання веде саме на нього', async () => {
    mockFetch();
    show();

    const link = within(await findRowOfState('Succeeded')).getByRole('link', {
      name: '⟦jobs.resultDownload⟧',
    });

    /*
     * ⛔ Мутаційний доказ: підставте замість `job.resultUrl` побудову адреси
     * з інших полів (`documentId`, `message`) — і `href` перестане
     * збігатися з тим, що віддав сервер.
     */
    expect(link.getAttribute('href')).toBe('/api/v1/documents/42/export/abc-123');
  });

  it('мутація: resultUrl відсутній — посилання ігнорується', async () => {
    mockFetch();
    show();

    await findRowOfState('Failed');

    expect(
      within(rowOfState('Failed')).queryByRole('link', { name: '⟦jobs.resultDownload⟧' }),
    ).toBeNull();
    expect(
      within(rowOfState('Running')).queryByRole('link', { name: '⟦jobs.resultDownload⟧' }),
    ).toBeNull();
  });
});
