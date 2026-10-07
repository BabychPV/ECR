import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { JobsPage } from '@/pages/admin/JobsPage';
import { openDrawerOf, rowOfState } from './jobsTestKit';

/**
 * BE-02: скасування фонової задачі з екрана.
 *
 * ⛔ Серверна частина (`CancelJobHandler`, `POST /api/v1/jobs/{jobId}/cancel`)
 * і клієнтський хук `useCancelJob` існували, а КЛІКНУТИ не було де: сторож
 * `Кожна_дія_сервера_має_споживача_в_інтерфейсі` задовольняється літеральним
 * шляхом у хуку, тож нічого не червоніло — а двадцятихвилинний перерахунок,
 * запущений помилково, спинити було НІЯК.
 *
 * ⚠ Перелік станів у тестах узятий з сервера, не з голови:
 * `CancelJobHandler.Active` — рівно `["Queued", "Running"]`
 * (`IntegrationHandlers.cs`). Решта термінальні: сервер відповів би `409`
 * (`ECR-JOB-0409`), тобто кнопка на них — підтвердження, заздалегідь
 * приречене на відмову.
 */

/**
 * ⚠ `#` у `jobId` — не екзотика, а звичайний формат повторюваної задачі, і
 * саме на ньому падав крок 17 `smoke.ps1`: незакодований `#` в URL ПОЧИНАЄ
 * ФРАГМЕНТ, шлях обрізається до `/api/v1/jobs/IRecalculationJob`, сервер
 * віддає 404 — і виглядає це як «задачі немає», а не як зламана адреса.
 */
const RunningJobId = 'IRecalculationJob#42';

const jobs = [
  {
    jobCode: 'Ecr.Application.Ports.IRecalculationJob',
    jobId: RunningJobId,
    percent: 40,
    state: 'Running',
    updatedAt: '2026-09-19T10:00:00Z',
  },
  {
    jobCode: 'Ecr.Application.Ports.IExcelExportJob',
    jobId: 'IExcelExportJob-0123456789abcdef0123456789abcdef',
    percent: 100,
    state: 'Succeeded',
    updatedAt: '2026-09-19T09:00:00Z',
  },
];

/** Усі адреси, куди сторінка сходила методом POST. */
const posted: string[] = [];

/** Чи тримати відповідь на скасування «у польоті» (L9-37). */
let holdCancel = false;

function mockFetch(): void {
  posted.length = 0;

  vi.stubGlobal(
    'fetch',
    vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);

      if (init?.method === 'POST') posted.push(url);

      if (url.includes('/cancel')) {
        if (holdCancel) return new Promise<Response>(() => {});

        return Promise.resolve(
          new Response(JSON.stringify({ jobId: RunningJobId }), {
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
  holdCancel = false;
});

describe('JobsPage: скасування задачі зі шторки', () => {
  it('задача, що виконується: кнопка → підтвердження → POST із закодованим «#»', async () => {
    mockFetch();
    const user = userEvent.setup();
    show();

    const drawer = await openDrawerOf('Running');
    await user.click(await within(drawer).findByRole('button', { name: '⟦jobs.cancel⟧' }));

    // ⛔ Підтвердження — ОКРЕМИЙ крок, а не тост після факту: скасування
    // обриває роботу, яку вже почали рахувати, і випадковий клік коштує
    // двадцяти хвилин чужого часу.
    const dialog = await screen.findByRole('dialog', { name: '⟦jobs.cancel⟧' });
    expect(within(dialog).getByText('⟦jobs.cancelConfirm⟧')).toBeTruthy();

    // ⚠ Доки підтвердження не натиснуте — запиту немає. Інакше «підтвердження»
    // було б лише декорацією навколо вже надісланої дії.
    expect(posted).toEqual([]);

    await user.click(within(dialog).getByRole('button', { name: '⟦jobs.cancel⟧' }));

    await waitFor(() => expect(posted.length).toBe(1));

    // ⛔ Саме ЗАКОДОВАНИЙ `#` (`%23`), а не сирий: із сирим шлях обрізається
    // до `/api/v1/jobs/IRecalculationJob` і сервер віддає 404.
    expect(posted[0]).toContain('/api/v1/jobs/IRecalculationJob%2342/cancel');
    expect(posted[0]).not.toContain('#');
  });

  it('задача в термінальному стані: кнопки скасування немає', async () => {
    mockFetch();
    show();

    // ⛔ Мутаційний доказ: приберіть умову видимості за станом
    // (`isCancellable(openState) &&` у `JobsPage.tsx`) — і кнопка з'явиться в
    // шторці успішної задачі, а цей тест впаде. Шторка `Running` нижче
    // доводить, що кнопка взагалі рендериться і відсутність тут — не
    // «нічого не намалювалося».
    const done = await openDrawerOf('Succeeded');
    // ⚠ Підвал уже намальований (кнопка закриття шторки є), а скасування — ні.
    expect(within(done).queryByRole('button', { name: '⟦jobs.cancel⟧' })).toBeNull();

    const running = await openDrawerOf('Running');
    expect(await within(running).findByRole('button', { name: '⟦jobs.cancel⟧' })).toBeTruthy();

    // ⚠ У рядку переліку кнопок дій більше немає (UI-28).
    expect(within(rowOfState('Running')).queryByRole('button', { name: '⟦jobs.cancel⟧' })).toBeNull();
  });

  it('повторний клік підтвердження, поки запит у польоті, другого скасування не шле (L9-37)', async () => {
    mockFetch();
    holdCancel = true;
    const user = userEvent.setup();
    show();

    const drawer = await openDrawerOf('Running');
    await user.click(await within(drawer).findByRole('button', { name: '⟦jobs.cancel⟧' }));
    const dialog = await screen.findByRole('dialog', { name: '⟦jobs.cancel⟧' });

    await user.click(within(dialog).getByRole('button', { name: '⟦jobs.cancel⟧' }));

    // ⚠ Спінер (`usePendingLoading`) вмикається лише через 100 мс, тож кнопка
    // ще активна — саме в це вікно влучає подвійний клік чи повторний Enter.
    const pending = await within(dialog).findByRole('button', { name: '⟦jobs.cancelling⟧' });
    await user.click(pending);

    // ⛔ Мутація «прибрати `if (cancel.isPending) return;`» — два POST, червоний.
    await new Promise((resolve) => setTimeout(resolve, 20));
    expect(posted).toHaveLength(1);
  });
});
