import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, waitFor, within, fireEvent } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { JobsPage } from '@/pages/admin/JobsPage';
import { findRowOfState, openDrawerOf, rowOfState } from './jobsTestKit';

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

afterEach(() => {
  vi.unstubAllGlobals();
  posted.length = 0;
});

describe('JobsPage: «Повторити» у шторці задачі', () => {
  it('провалена задача — кнопка «Повторити» є й шле POST на /restart', async () => {
    mockFetch();
    show();

    const drawer = await openDrawerOf('Failed');
    fireEvent.click(await within(drawer).findByRole('button', { name: '⟦jobs.restart⟧' }));

    // ⚠ Той самий привід, що в `cancelJob.test.tsx`: `#` у `jobId` починає
    // фрагмент URL і мусить бути закодований.
    await waitFor(() => expect(posted.length).toBe(1));
    expect(posted[0]).toBe('/api/v1/jobs/IRecalculationJob%23failed-1/restart');
    expect(posted[0]).not.toContain('#');
  });

  it('мутація: задача не в термінальному невдалому стані — кнопки «Повторити» немає', async () => {
    mockFetch();
    show();

    /*
     * ⛔ Мутаційний доказ: приберіть умову видимості (`canRestartJob` /
     * `state === 'Failed'`) — і кнопка з'явиться в шторці `Running` та
     * `Succeeded`, а цей тест впаде. Шторка `Failed` доводить, що кнопка
     * взагалі рендериться.
     */
    // ⚠ Підвал шторки вже є: у `Running` — «Cancel job», у `Succeeded` — файл.
    const running = await openDrawerOf('Running');
    await within(running).findByRole('button', { name: '⟦jobs.cancel⟧' });
    expect(within(running).queryByRole('button', { name: '⟦jobs.restart⟧' })).toBeNull();

    const done = await openDrawerOf('Succeeded');
    await within(done).findByRole('link', { name: '⟦jobs.resultDownload⟧' });
    expect(within(done).queryByRole('button', { name: '⟦jobs.restart⟧' })).toBeNull();

    const failed = await openDrawerOf('Failed');
    expect(await within(failed).findByRole('button', { name: '⟦jobs.restart⟧' })).toBeTruthy();

    // ⚠ У рядку переліку кнопок дій більше немає (UI-28).
    expect(within(rowOfState('Failed')).queryByRole('button', { name: '⟦jobs.restart⟧' })).toBeNull();
  });
});

describe('JobsPage: посилання на файл результату у шторці задачі', () => {
  it('resultUrl не null — посилання веде саме на нього', async () => {
    mockFetch();
    show();

    const drawer = await openDrawerOf('Succeeded');
    const link = await within(drawer).findByRole('link', { name: '⟦jobs.resultDownload⟧' });

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

    const failed = await openDrawerOf('Failed');
    await within(failed).findByRole('button', { name: '⟦jobs.restart⟧' });
    expect(within(failed).queryByRole('link', { name: '⟦jobs.resultDownload⟧' })).toBeNull();

    const running = await openDrawerOf('Running');
    await within(running).findByRole('button', { name: '⟦jobs.cancel⟧' });
    expect(within(running).queryByRole('link', { name: '⟦jobs.resultDownload⟧' })).toBeNull();

    await findRowOfState('Succeeded');
  });
});
