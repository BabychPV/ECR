import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { JobsPage } from '@/pages/admin/JobsPage';

/**
 * Поля `BE-08` на екрані задач: спроба, причина провалу, кореляція,
 * документ, автор, повідомлення.
 *
 * ⚠ Каталог тут не завантажено, тож `t()` віддає позначений ключ із
 * параметрами (`⟦jobs.attemptOf (n=3, max=4)⟧`) — саме він доводить, що
 * текст пройшов через каталог, і з якими числами.
 *
 * ⚠ Коди — лише наявні в `ErrorCodes.cs` (`ECR-JOB-0409`).
 */

const failedStatus = {
  jobId: 'IExcelExportJob-a1',
  state: 'Failed',
  percent: 40,
  message: null,
  error: null,
  attempt: 3,
  maxAttempts: 4,
  errorCode: 'ECR-JOB-0409',
  correlationId: 'corr-7f3a',
  createdAt: '2026-09-20T08:00:00Z',
  documentId: 42,
};

const list = [
  {
    jobCode: 'Ecr.Application.Ports.IRecalculationJob',
    jobId: 'IRecalculationJob-r2',
    percent: 50,
    startedAt: '2026-09-20T09:00:00Z',
    updatedAt: '2026-09-20T09:01:00Z',
    state: 'Running',
    attempt: 2,
    correlationId: 'corr-r2',
    createdByDisplayName: null,
    message: 'Перераховано 5 комірок',
    createdAt: '2026-09-20T08:59:00Z',
    errorCode: null,
    documentId: null,
  },
  {
    jobCode: 'Ecr.Application.Ports.IExcelImportJob',
    jobId: 'IExcelImportJob-f1',
    percent: 10,
    startedAt: '2026-09-20T07:00:00Z',
    updatedAt: '2026-09-20T07:01:00Z',
    state: 'Failed',
    attempt: 1,
    correlationId: 'corr-f1',
    createdByDisplayName: 'Олена Коваль',
    message: null,
    createdAt: '2026-09-20T06:59:00Z',
    errorCode: null,
    documentId: 77,
  },
];

function mockFetch(status: unknown, rows: unknown[]): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      const body = /\/api\/v1\/jobs\/[^/?]+/.test(url) ? status : rows;

      return new Response(JSON.stringify(body), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      });
    }),
  );
}

function show(entry: string): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider>
      <MemoryRouter initialEntries={[entry]}>
        <QueryClientProvider client={client}>
          <JobsPage />
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

function card(): HTMLElement {
  const title = screen.getByText(/IExcelExportJob|ExcelExport|jobs\.kind/);

  return title.closest('.mantine-Card-root') as HTMLElement;
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('JobsPage: картка задачі — поля BE-08', () => {
  it('провал: спроба N з M, код через каталог, кореляція з копіюванням, документ', async () => {
    mockFetch(failedStatus, []);
    show('/admin/jobs?id=IExcelExportJob-a1');

    expect(await screen.findByText('⟦jobs.attemptOf (n=3, max=4)⟧')).toBeTruthy();

    // Код — рядком каталогу, не сирим кодом.
    expect(screen.getByText('⟦err.ECR-JOB-0409⟧')).toBeTruthy();
    expect(screen.queryByText('ECR-JOB-0409')).toBeNull();

    expect(screen.getByText('corr-7f3a')).toBeTruthy();
    expect(
      await screen.findByRole('button', { name: '⟦jobs.copyCorrelation⟧' }),
    ).toBeTruthy();

    const link = screen.getByRole('link', { name: '⟦jobs.openDocument (id=42)⟧' });
    expect(link.getAttribute('href')).toBe('/documents/42');

    expect(card().querySelector('time[datetime="2026-09-20T08:00:00Z"]')).not.toBeNull();
  });

  it('maxAttempts = null — «спроба N» без «з M»', async () => {
    mockFetch({ ...failedStatus, maxAttempts: null }, []);
    show('/admin/jobs?id=IExcelExportJob-a1');

    expect(await screen.findByText('⟦jobs.attempt (n=3)⟧')).toBeTruthy();
    expect(screen.queryByText(/jobs\.attemptOf/)).toBeNull();
  });

  it('провал без errorCode — «причину не записано», а не порожнеча', async () => {
    mockFetch({ ...failedStatus, errorCode: null }, []);
    show('/admin/jobs?id=IExcelExportJob-a1');

    expect(await screen.findByText('⟦jobs.failureUnrecorded⟧')).toBeTruthy();
  });

  it('дзеркало: успішна задача з першої спроби — ані спроби, ані блоку помилки', async () => {
    mockFetch(
      {
        ...failedStatus,
        state: 'Succeeded',
        percent: 100,
        attempt: 1,
        errorCode: null,
        documentId: null,
      },
      [],
    );
    show('/admin/jobs?id=IExcelExportJob-a1');

    await screen.findByText(/ExcelExport|jobs\.kind/);

    expect(document.querySelector('[data-job-attempt]')).toBeNull();
    expect(document.querySelector('[data-job-failure]')).toBeNull();
    expect(screen.queryByText(/jobs\.attempt/)).toBeNull();
    expect(screen.queryByText(/jobs\.failureUnrecorded|err\./)).toBeNull();
    expect(screen.queryByRole('link', { name: /jobs\.openDocument/ })).toBeNull();
  });
});

describe('JobsPage: перелік задач — поля BE-08', () => {
  it('спроба, автор, повідомлення як є, документ, провал без коду', async () => {
    mockFetch(null, list);
    show('/admin/jobs');

    // Спроба 2 — показана, у переліку `maxAttempts` немає — без «з M».
    expect(await screen.findByText('⟦jobs.attempt (n=2)⟧')).toBeTruthy();
    expect(screen.queryByText(/jobs\.attemptOf/)).toBeNull();

    // Спроба 1 — нічого.
    expect(document.querySelectorAll('[data-job-attempt]')).toHaveLength(1);

    // Повідомлення вже перекладене сервером — як є, не через `t()`.
    expect(screen.getByText('Перераховано 5 комірок')).toBeTruthy();
    expect(screen.queryByText(/⟦Перераховано/)).toBeNull();

    // Автор: null — система.
    expect(screen.getByText('⟦jobs.system⟧')).toBeTruthy();
    expect(screen.getByText('Олена Коваль')).toBeTruthy();

    const link = screen.getByRole('link', { name: '⟦jobs.openDocument (id=77)⟧' });
    expect(link.getAttribute('href')).toBe('/documents/77');

    expect(screen.getByText('⟦jobs.failureUnrecorded⟧')).toBeTruthy();
    expect(document.querySelectorAll('[data-job-failure]')).toHaveLength(1);

    await waitFor(() =>
      expect(document.querySelector('time[datetime="2026-09-20T08:59:00Z"]')).not.toBeNull(),
    );

    // L5: не більше семи колонок.
    expect(document.querySelectorAll('thead th').length).toBeLessThanOrEqual(7);
  });
});
