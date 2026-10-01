import { afterEach, describe, expect, it, vi } from 'vitest';
import { configure, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { PeriodsPage } from '@/pages/admin/PeriodsPage';
import { testTheme } from '@/test/render';

/**
 * Сторінка періодів: стани «помилка», «порожньо», «завантаження» двох
 * основних запитів — перелік проєктів і календар періодів (`ФВ-14.22`).
 *
 * ⚠ Проєктів у фікстурі ДВА: з одним спрацював би автовибір (`U-10`) і
 * підмішав би запит календаря туди, де перевіряється лише перелік.
 */
configure({ asyncUtilTimeout: 10_000 });

const projects = [
  { id: 1, code: 'PRJ_ONE', status: 'Active' },
  { id: 2, code: 'PRJ_TWO', status: 'Active' },
];

const json = (body: unknown): Promise<Response> =>
  Promise.resolve(new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } }));

const problem = (status: number, errorCode: string): Promise<Response> =>
  Promise.resolve(
    new Response(
      JSON.stringify({
        title: status === 403 ? 'Forbidden' : 'Server error',
        status,
        errorCode,
        correlationId: 'corr-periods',
        detail: null,
      }),
      { status, headers: { 'Content-Type': 'application/problem+json' } },
    ),
  );

const pending = (): Promise<Response> => new Promise<Response>(() => undefined);

/** Адреси, куди сторінка сходила. */
let requested: string[] = [];

function serve(answer: { projects: () => Promise<Response>; periods?: () => Promise<Response> }): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      requested.push(url);

      if (url.includes('/api/v1/me')) {
        return json({
          denies: [],
          grants: {},
          isSimulation: false,
          language: 'en',
          mustChangePassword: false,
          permissions: [],
          simulatedForUserId: null,
          userId: 1,
          userName: 'tester',
        });
      }

      // ⚠ Календар — раніше за перелік: його адреса містить адресу переліку.
      if (/\/api\/v1\/projects\/\d+\/periods/.test(url)) return (answer.periods ?? pending)();
      if (url.includes('/api/v1/projects')) return answer.projects();

      return json(null);
    }),
  );
}

function show(path = '/admin/periods'): void {
  render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter initialEntries={[path]}>
        <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
          <PeriodsPage />
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

afterEach(() => {
  requested = [];
  vi.unstubAllGlobals();
});

describe('PeriodsPage — стани переліку проєктів', () => {
  it('500: показує помилку з кодом, а не «проєктів немає»', async () => {
    serve({ projects: () => problem(500, 'ECR-SYS-0500') });
    show();

    expect(await screen.findByText('ECR-SYS-0500', { exact: false })).toBeTruthy();
    expect(screen.queryByText('⟦periods.noProjects⟧')).toBeNull();
  });

  it('403: стан «немає права», а не «проєктів немає»', async () => {
    serve({ projects: () => problem(403, 'ECR-AUTH-0403') });
    show();

    const alerts = await screen.findAllByRole('alert');
    expect(alerts.map((a) => a.textContent).join(' ')).toContain('ECR-AUTH-0403');
    expect(screen.queryByText('⟦periods.noProjects⟧')).toBeNull();
  });

  it('порожній перелік: пояснення «проєктів немає» і жодного коду помилки', async () => {
    serve({ projects: () => json({ items: [], nextCursor: null, totalCount: 0 }) });
    show();

    expect(await screen.findByText('⟦periods.noProjects⟧')).toBeTruthy();
    expect(screen.queryByText('ECR-', { exact: false })).toBeNull();
  });

  it('у дорозі: «завантаження», а не «проєктів немає»', async () => {
    serve({ projects: pending });
    show();

    const busy = await screen.findAllByRole('status');
    expect(busy.some((node) => node.getAttribute('aria-busy') === 'true')).toBe(true);
    expect(screen.queryByText('⟦periods.noProjects⟧')).toBeNull();
  });
});

describe('PeriodsPage — стани календаря обраного проєкту', () => {
  const listed = (): Promise<Response> => json({ items: projects, nextCursor: null, totalCount: projects.length });

  it('500: показує помилку з кодом, а не «періодів немає»', async () => {
    serve({ projects: listed, periods: () => problem(500, 'ECR-SYS-0500') });
    show('/admin/periods?projectId=1');

    expect(await screen.findByText('ECR-SYS-0500', { exact: false })).toBeTruthy();
    expect(screen.queryByText('⟦periods.noPeriods⟧')).toBeNull();
  });

  it('403: стан «немає права», а не «періодів немає»', async () => {
    serve({ projects: listed, periods: () => problem(403, 'ECR-AUTH-0403') });
    show('/admin/periods?projectId=1');

    const alerts = await screen.findAllByRole('alert');
    expect(alerts.map((a) => a.textContent).join(' ')).toContain('ECR-AUTH-0403');
    expect(screen.queryByText('⟦periods.noPeriods⟧')).toBeNull();
  });

  it('порожній календар: пояснення «періодів немає» і жодної таблиці', async () => {
    serve({
      projects: listed,
      periods: () =>
        json({
          timeZoneId: 'Asia/Aqtau',
          policy: { code: 'ECR-Standard', openOffsetDays: 0, graceOffsetDays: 15, hardCloseOffsetDays: 45 },
          periods: [],
        }),
    });
    show('/admin/periods?projectId=1');

    expect(await screen.findByText('⟦periods.noPeriods⟧')).toBeTruthy();
    expect(screen.queryByRole('table')).toBeNull();
  });

  it('у дорозі: «завантаження», а не «періодів немає»', async () => {
    serve({ projects: listed, periods: pending });
    show('/admin/periods?projectId=1');

    // Запит календаря справді пішов — у дорозі саме він.
    await waitFor(() => {
      expect(requested.some((url) => url.includes('/api/v1/projects/1/periods'))).toBe(true);
    });
    const busy = await screen.findAllByRole('status');
    expect(busy.some((node) => node.getAttribute('aria-busy') === 'true')).toBe(true);
    expect(screen.queryByText('⟦periods.noPeriods⟧')).toBeNull();
  });
});
