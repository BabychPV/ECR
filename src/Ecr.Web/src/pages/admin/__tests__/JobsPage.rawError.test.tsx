import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { JobsPage } from '@/pages/admin/JobsPage';

/**
 * `X-04`: картка задачі на `/admin/jobs?id=…` показувала сирий `error`
 * (`ex.Message` сервера — «Violation of PRIMARY KEY…» чи українське речення)
 * видимим рядком ПІД локалізованою причиною `JobFailure`. Тобто одна відмова
 * — двічі, і вдруге чужою мовою.
 *
 * ⚠ Сирий текст не зникає — він під згорнутим «технічні подробиці»: екран
 * відкривається лише з `System.ViewHealth`, і адміністраторові, що розбирає
 * збій, його треба скопіювати.
 */

const Raw = 'Violation of PRIMARY KEY constraint PK_CellValue. Cannot insert duplicate key.';

function mockFetch(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn((input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('/api/v1/jobs/job-7')) {
        return Promise.resolve(
          new Response(
            JSON.stringify({
              jobId: 'job-7',
              state: 'Failed',
              percent: 30,
              message: null,
              error: Raw,
              errorCode: 'ECR-PRD-0409',
              correlationId: 'cid-7',
            }),
            { status: 200, headers: { 'Content-Type': 'application/json' } },
          ),
        );
      }

      return Promise.resolve(
        new Response(JSON.stringify([]), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        }),
      );
    }),
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('JobsPage: сирий текст провалу задачі', () => {
  it('причина — з каталогу; сирий текст лише всередині згорнутих подробиць', async () => {
    mockFetch();

    render(
      <MantineProvider>
        <MemoryRouter initialEntries={['/admin/jobs?id=job-7']}>
          <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
            <JobsPage />
          </QueryClientProvider>
        </MemoryRouter>
      </MantineProvider>,
    );

    // Локалізована причина (`JobFailure`: код → каталог).
    await waitFor(() => expect(screen.getByText('⟦err.ECR-PRD-0409⟧')).toBeTruthy());

    const raw = screen.getByText(Raw);

    /*
     * ⛔ Головне твердження. Мутація «повернути `<Text>{status.error}</Text>`»
     * лишає сирий рядок ПОЗА `<details>` — і `closest` дає `null`.
     */
    const details = raw.closest('details');
    expect(details).not.toBeNull();
    expect(details?.open).toBe(false);
    expect(details?.querySelector('summary')?.textContent).toBe('⟦common.technicalDetails⟧');
  });
});
