import { afterEach, describe, expect, it, vi } from 'vitest';
import { configure, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { RecalculationApprovalsPanel } from '@/features/projects/RecalculationApprovals';
import { testTheme } from '@/test/render';

/**
 * Панель погоджень перерахунку (ФВ-9.7): стани «помилка», «порожньо»,
 * «завантаження» (`ФВ-14.22`).
 *
 * ⛔ Дефект, який ці тести закрили: рядки бралися як
 * `Array.isArray(approvals.data) ? approvals.data : []`, тож і відмова сервера
 * (500, 403), і запит у дорозі показували «погоджень немає». Для «чотирьох
 * очей» це не косметика: друга людина, якій не вдалося прочитати перелік,
 * бачила «нічого не чекає на вас» і не підтверджувала чужий запит.
 */
configure({ asyncUtilTimeout: 10_000 });

const Approvals = '/api/v1/projects/7/recalculation-approvals';

function serve(approvals: () => Promise<Response>): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      if (url.endsWith(Approvals)) return approvals();

      // Сесія: панелі вона потрібна лише для кнопок у рядках.
      return new Response(
        JSON.stringify({
          denies: [],
          grants: { 'Project:7': 'Manage' },
          isSimulation: false,
          language: 'en',
          mustChangePassword: false,
          permissions: ['Calculation.Recalculate'],
          simulatedForUserId: null,
          userId: 1,
          userName: 'me',
        }),
        { status: 200, headers: { 'Content-Type': 'application/json' } },
      );
    }),
  );
}

const problem = (status: number, errorCode: string): Promise<Response> =>
  Promise.resolve(
    new Response(
      JSON.stringify({
        title: status === 403 ? 'Forbidden' : 'Server error',
        status,
        errorCode,
        correlationId: 'corr-approvals',
        detail: null,
      }),
      { status, headers: { 'Content-Type': 'application/problem+json' } },
    ),
  );

function show(): void {
  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <RecalculationApprovalsPanel projectId={7} />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('RecalculationApprovalsPanel — стани', () => {
  it('500: показує помилку з кодом, а не «погоджень немає»', async () => {
    serve(() => problem(500, 'ECR-SYS-0500'));
    show();

    expect(await screen.findByText('ECR-SYS-0500', { exact: false })).toBeTruthy();
    expect(screen.queryByText('⟦recalcApprovals.empty⟧')).toBeNull();
  });

  it('403: стан «немає права», а не «погоджень немає»', async () => {
    serve(() => problem(403, 'ECR-AUTH-0403'));
    show();

    expect((await screen.findByRole('alert')).textContent).toContain('ECR-AUTH-0403');
    expect(screen.queryByText('⟦recalcApprovals.empty⟧')).toBeNull();
  });

  it('порожній перелік: пояснення «погоджень немає» і жодної таблиці', async () => {
    serve(() =>
      Promise.resolve(new Response('[]', { status: 200, headers: { 'Content-Type': 'application/json' } })),
    );
    show();

    expect(await screen.findByText('⟦recalcApprovals.empty⟧')).toBeTruthy();
    expect(screen.queryByRole('table')).toBeNull();
  });

  it('у дорозі: «завантаження», а не «погоджень немає»', async () => {
    serve(() => new Promise<Response>(() => undefined));
    show();

    expect((await screen.findByRole('status')).getAttribute('aria-busy')).toBe('true');
    expect(screen.queryByText('⟦recalcApprovals.empty⟧')).toBeNull();
  });
});
