import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { JobsPage } from '@/pages/admin/JobsPage';

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

function mockFetch(): void {
  posted.length = 0;

  vi.stubGlobal(
    'fetch',
    vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);

      if (init?.method === 'POST') posted.push(url);

      if (url.includes('/cancel')) {
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

/**
 * Рядок переліку, впізнаний за значком стану.
 *
 * ✎ 2026-09-19. Тут стояло `screen.getByText(state)` — пошук за КОДОМ СЕРВЕРА
 * (`Running`, `Succeeded`) як за видимим текстом. Відколи стан малює
 * `StatusBadge`, підпис береться з каталогу (`status.job.*`), а не з коду:
 * це та зміна поведінки, заради якої набір і заведено — код сервера не є
 * текстом інтерфейсу й не перекладається. Локатор переведено на
 * `data-status-state`, який набір кладе в розмітку саме для тестів і e2e.
 *
 * ⚠ Це не послаблення: атрибут прив'язаний до стану ТОЧНО, тоді як пошук за
 * текстом збігся б і з будь-яким іншим вузлом, що містить те саме слово.
 */
function rowOfState(state: string): HTMLElement {
  const row = document.querySelector(`[data-status-state="${state}"]`)?.closest('tr') ?? null;
  expect(row, `рядок задачі у стані «${state}»`).not.toBeNull();

  return row as HTMLElement;
}

/** Чекає, доки в переліку з'явиться рядок у цьому стані. */
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

describe('JobsPage: скасування задачі з переліку', () => {
  it('задача, що виконується: кнопка → підтвердження → POST із закодованим «#»', async () => {
    mockFetch();
    const user = userEvent.setup();
    show();

    const cancelInRow = within(await findRowOfState('Running')).getByRole('button', {
      name: '⟦jobs.cancel⟧',
    });

    await user.click(cancelInRow);

    // ⛔ Підтвердження — ОКРЕМИЙ крок, а не тост після факту: скасування
    // обриває роботу, яку вже почали рахувати, і випадковий клік коштує
    // двадцяти хвилин чужого часу.
    const dialog = await screen.findByRole('dialog');
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
    // (`isCancellable(job.state) &&` у `JobsPage.tsx`) — і кнопка з'явиться в
    // рядку успішної задачі, а цей тест впаде. Сусідній рядок `Running`
    // доводить, що кнопка взагалі рендериться і відсутність тут — не
    // «нічого не намалювалося».
    expect(
      within(await findRowOfState('Succeeded')).queryByRole('button', { name: '⟦jobs.cancel⟧' }),
    ).toBeNull();

    expect(
      within(rowOfState('Running')).getByRole('button', { name: '⟦jobs.cancel⟧' }),
    ).toBeTruthy();
  });
});
