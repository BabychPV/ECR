import { afterEach, describe, expect, it, vi } from 'vitest';
import { configure, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { JobsPage } from '@/pages/admin/JobsPage';
import { testTheme } from '@/test/render';

/**
 * Фонові задачі: стани журналу задач і картки стеження (`ФВ-14.22`).
 *
 * ⚠ Порожній журнал уже стереже `JobsPage.singleEmptyState.test.tsx` (U-09).
 * Тут — решта: відмова (500/403) і запит у дорозі НЕ виглядають як «задач
 * ще не було», а невідомий ідентифікатор картки — помилка з кодом, а не
 * вічний прогрес.
 */
configure({ asyncUtilTimeout: 10_000 });

const ok = (body: unknown): Promise<Response> =>
  Promise.resolve(new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } }));

const problem = (status: number, errorCode: string): Promise<Response> =>
  Promise.resolve(
    new Response(
      JSON.stringify({
        title: status === 403 ? 'Forbidden' : 'Server error',
        status,
        errorCode,
        correlationId: 'corr-jobs',
        detail: null,
      }),
      { status, headers: { 'Content-Type': 'application/problem+json' } },
    ),
  );

const never = (): Promise<Response> => new Promise<Response>(() => undefined);

interface Plan {
  readonly list: () => Promise<Response>;
  readonly job?: () => Promise<Response>;
}

function serve(plan: Plan): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      // ⚠ Спершу картка (`/api/v1/jobs/{id}`), потім перелік (`/api/v1/jobs?…`).
      if (/\/api\/v1\/jobs\/[^?]/.test(url)) return (plan.job ?? never)();
      if (url.includes('/api/v1/jobs')) return plan.list();

      return ok(null);
    }),
  );
}

function show(path = '/admin/jobs'): void {
  render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter initialEntries={[path]}>
        <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
          <JobsPage />
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

async function alertWithCode(code: string): Promise<HTMLElement> {
  return waitFor(() => {
    const found = screen.queryAllByRole('alert').find((node) => (node.textContent ?? '').includes(code));
    expect(found).toBeTruthy();
    return found as HTMLElement;
  });
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('JobsPage — стани журналу задач', () => {
  it('500: помилка з кодом, а не «задач ще не було»', async () => {
    serve({ list: () => problem(500, 'ECR-SYS-0500') });
    show();

    await alertWithCode('ECR-SYS-0500');
    expect(screen.queryByText('⟦jobs.recentEmpty⟧')).toBeNull();
    expect(screen.queryByRole('table')).toBeNull();
  });

  it('403: «немає права» з кодом, а не «задач ще не було»', async () => {
    serve({ list: () => problem(403, 'ECR-AUTH-0403') });
    show();

    await alertWithCode('ECR-AUTH-0403');
    expect(screen.queryByText('⟦jobs.recentEmpty⟧')).toBeNull();
  });

  it('дзеркало: порожній журнал — «задач ще не було», без помилки', async () => {
    serve({ list: () => ok([]) });
    show();

    expect(await screen.findByText('⟦jobs.recentEmpty⟧')).toBeTruthy();
    expect(screen.queryByRole('alert')).toBeNull();
  });

  it('у дорозі: «завантаження», а не «задач ще не було»', async () => {
    serve({ list: never });
    show();

    expect((await screen.findByRole('status')).getAttribute('aria-busy')).toBe('true');
    expect(screen.queryByText('⟦jobs.recentEmpty⟧')).toBeNull();
  });
});

describe('JobsPage — стани картки стеження', () => {
  it('невідомий ідентифікатор (404): помилка з кодом, а не вічний прогрес', async () => {
    serve({ list: () => ok([]), job: () => problem(404, 'ECR-JOB-0404') });
    show('/admin/jobs?id=missing-job');

    await alertWithCode('ECR-JOB-0404');
    // Журнал свій стан тримає окремо — і він саме «порожньо», не «вантажиться».
    expect(await screen.findByText('⟦jobs.recentEmpty⟧')).toBeTruthy();
    expect(screen.queryByRole('status')).toBeNull();
  });

  it('картка в дорозі: «завантаження», а не заглушка «введіть ідентифікатор»', async () => {
    serve({ list: () => ok([]), job: never });
    show('/admin/jobs?id=slow-job');

    // Журнал відповів — отже, єдиний «status» на екрані належить картці.
    expect(await screen.findByText('⟦jobs.recentEmpty⟧')).toBeTruthy();
    expect((await screen.findByRole('status')).getAttribute('aria-busy')).toBe('true');
    expect(screen.queryByRole('heading', { name: '⟦jobs.pick⟧' })).toBeNull();
    expect(screen.queryByRole('alert')).toBeNull();
  });
});
