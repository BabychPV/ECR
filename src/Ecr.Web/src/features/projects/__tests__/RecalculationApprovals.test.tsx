import type { JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { configure, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { EcrApiError } from '@/api/client';
import {
  RecalculationApprovalsPanel,
  RequestRecalculationButton,
} from '@/features/projects/RecalculationApprovals';
import { showApiError } from '@/shared/ui/notify';
import { testTheme } from '@/test/render';

/**
 * Екран погоджень перерахунку закритого періоду (ФВ-9.7, аудит S1).
 *
 * ⛔ Головне: друга людина — це сесія, а не поле. Тому тести дивляться на
 * ТІЛО запитів (жодного `approvedByUserId`) і на те, кому яка кнопка дісталась.
 */
vi.mock('@/shared/ui/notify', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/ui/notify')>()),
  showApiError: vi.fn(),
  showDone: vi.fn(),
}));

configure({ asyncUtilTimeout: 20_000 });

const Me = 1;
const Other = 2;

const approval = (id: number, requestedBy: number, confirmedBy: number | null) => ({
  id,
  periodKey: 202601,
  reason: `reason ${String(id)}`,
  requestedByUserId: requestedBy,
  requestedByName: `user ${String(requestedBy)}`,
  requestedAt: '2026-09-28T08:00:00Z',
  expiresAt: '2026-09-29T08:00:00Z',
  confirmedByUserId: confirmedBy,
  confirmedByName: confirmedBy === null ? null : `user ${String(confirmedBy)}`,
  confirmedAt: confirmedBy === null ? null : '2026-09-28T09:00:00Z',
});

const sent: { url: string; method: string; body: unknown }[] = [];

function server(options: { confirmStatus?: number } = {}): void {
  sent.length = 0;

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const method = String(init?.method ?? 'GET');
      const body: unknown = typeof init?.body === 'string' ? JSON.parse(init.body) : null;
      const json = (value: unknown, status = 200, type = 'application/json'): Response =>
        new Response(JSON.stringify(value), { status, headers: { 'Content-Type': type } });

      if (method !== 'GET') sent.push({ url, method, body });

      if (url.includes('/api/v1/me')) {
        return json({
          denies: [],
          grants: { 'Project:7': 'Manage' },
          isSimulation: false,
          language: 'en',
          mustChangePassword: false,
          permissions: ['Calculation.Recalculate'],
          simulatedForUserId: null,
          userId: Me,
          userName: 'me',
        });
      }

      if (url.endsWith('/confirm')) {
        const status = options.confirmStatus ?? 200;
        return status === 200
          ? json(approval(11, Other, Me))
          : json(
              {
                title: 'Conflict',
                status,
                errorCode: 'ECR-CALC-0409',
                correlationId: 'cid',
                detail: 'This recalculation approval is not waiting for confirmation.',
                messageKey: 'err.ECR-CALC-0409.approvalNotPending',
              },
              status,
              'application/problem+json',
            );
      }

      if (url.endsWith('/recalculate')) return json({ jobId: 'IRecalculationJob-1', projectId: 7, periodKey: 202601 }, 202);

      if (url.endsWith('/recalculation-approvals')) {
        return method === 'POST'
          ? json(approval(20, Me, null), 201)
          : json([approval(11, Other, null), approval(12, Me, null), approval(13, Me, Other)]);
      }

      return json(null);
    }),
  );
}

function show(ui: JSX.Element): void {
  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        {ui}
      </QueryClientProvider>
    </MantineProvider>,
  );
}

const rowOf = async (reason: string): Promise<HTMLElement> =>
  (await screen.findByText(reason)).closest('tr') as HTMLElement;

afterEach(() => {
  vi.unstubAllGlobals();
  vi.mocked(showApiError).mockClear();
});

describe('RequestRecalculationButton', () => {
  it('запит шле період і причину — і нічого про погоджувача', async () => {
    server();
    show(<RequestRecalculationButton projectId={7} periodKey={202601} />);

    fireEvent.click(screen.getByRole('button', { name: '⟦recalcApprovals.request⟧' }));
    const dialog = await screen.findByRole('dialog');
    fireEvent.change(within(dialog).getByRole('textbox'), { target: { value: 'Коефіцієнт, лист №17' } });
    fireEvent.click(within(dialog).getByRole('button', { name: '⟦recalcApprovals.request⟧' }));

    await waitFor(() =>
      expect(sent).toEqual([
        {
          url: '/api/v1/projects/7/recalculation-approvals',
          method: 'POST',
          body: { periodKey: 202601, reason: 'Коефіцієнт, лист №17' },
        },
      ]),
    );
  });
});

describe('RecalculationApprovalsPanel', () => {
  it('«Підтвердити» — лише на чужому очікуваному; на власному її немає', async () => {
    server();
    show(<RecalculationApprovalsPanel projectId={7} />);

    const others = await rowOf('reason 11');
    const mine = await rowOf('reason 12');

    await waitFor(() =>
      expect(within(others).getByRole('button', { name: '⟦recalcApprovals.confirm⟧' })).toBeTruthy(),
    );
    // ⛔ Мутація «без умови mine» показує кнопку й тут — сервер дав би 409.
    expect(within(mine).queryByRole('button', { name: '⟦recalcApprovals.confirm⟧' })).toBeNull();
    expect(within(mine).queryByRole('button', { name: '⟦workflow.recalculate⟧' })).toBeNull();
  });

  it('підтвердження чужого — POST …/confirm без тіла', async () => {
    server();
    show(<RecalculationApprovalsPanel projectId={7} />);

    const others = await rowOf('reason 11');
    fireEvent.click(await within(others).findByRole('button', { name: '⟦recalcApprovals.confirm⟧' }));

    await waitFor(() =>
      expect(sent).toEqual([
        { url: '/api/v1/projects/7/recalculation-approvals/11/confirm', method: 'POST', body: null },
      ]),
    );
  });

  it('власне підтверджене — перерахунок із approvalId свого періоду', async () => {
    server();
    show(<RecalculationApprovalsPanel projectId={7} />);

    const confirmed = await rowOf('reason 13');
    fireEvent.click(await within(confirmed).findByRole('button', { name: '⟦workflow.recalculate⟧' }));

    await waitFor(() =>
      expect(sent).toEqual([
        { url: '/api/v1/projects/7/recalculate', method: 'POST', body: { periodKey: 202601, approvalId: 13 } },
      ]),
    );
  });

  it('409 від сервера — через showApiError з кодом і messageKey', async () => {
    server({ confirmStatus: 409 });
    show(<RecalculationApprovalsPanel projectId={7} />);

    const others = await rowOf('reason 11');
    fireEvent.click(await within(others).findByRole('button', { name: '⟦recalcApprovals.confirm⟧' }));

    await waitFor(() => expect(showApiError).toHaveBeenCalledTimes(1));
    const error = vi.mocked(showApiError).mock.calls[0]?.[0];
    expect(error).toBeInstanceOf(EcrApiError);
    expect((error as EcrApiError).problem.errorCode).toBe('ECR-CALC-0409');
    expect(JSON.stringify((error as EcrApiError).problem)).toContain('err.ECR-CALC-0409.approvalNotPending');
  });
});
