import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { DocumentActionBar } from '../DocumentActionBar';
import { markedMonthColumns } from '@/features/workflow/api';

/**
 * D-441 (AN-128): «Подати» подає НАЙРАНІШИЙ неподаний період у стані Open/Grace (`GET …/submission-target`),
 * а не відкритий на екрані. У лютому з неподаним січнем кнопка — «Подати: січень», попередження «наступний —
 * лютий», клік шле `periodKey = 202601`; ▾ дає вибрати лютий.
 *
 * ⛔ На старому коді кнопка подавала відкритий період (202602) і не мала ні вибору, ні попередження.
 */

const Jan = 202601;
const Feb = 202602;

const CurrentUser = {
  denies: [],
  grants: { 'Project:7': 'Approve' },
  isSimulation: false,
  language: 'en',
  mustChangePassword: false,
  permissions: ['Document.View'],
  simulatedForUserId: null,
  userId: 9,
  userName: 'author',
};

const json = (body: unknown, status = 200): Response =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });

let submits: { periodKey?: number; sheetDefId?: number }[] = [];

function mockFetch(janStatus: string): void {
  submits = [];

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);

      if (url.includes('/api/v1/me')) return json(CurrentUser);

      // ⚠ Раніше за `/submit`: `/submission-target` містить і його.
      if (url.includes('/submission-target')) {
        const janOpen = janStatus === 'Draft' || janStatus === 'Rejected';

        return json({
          targetPeriodKey: janOpen ? Jan : Feb,
          nextPeriodKey: janOpen ? Feb : null,
          periods: [
            { periodKey: Jan, state: 'Grace', sheetStatus: janStatus, submittable: janOpen },
            { periodKey: Feb, state: 'Open', sheetStatus: 'Draft', submittable: true },
          ],
          monthColumns: [],
        });
      }

      if (url.includes('/submit') && init?.method === 'POST') {
        submits.push(JSON.parse(String(init.body)) as { periodKey?: number; sheetDefId?: number });

        return new Response(null, { status: 204 });
      }

      if (url.includes('/recall')) return json({ canRecall: false });
      if (url.includes('/workflow/history')) return json([]);

      return json(null);
    }),
  );
}

function show(state = 'Draft', janStatus = 'Draft'): void {
  mockFetch(janStatus);

  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  client.setQueryData(['document', 1, Feb], {
    businessKey: 'DOC-1',
    createdAt: '2026-01-01T00:00:00Z',
    id: 1,
    nameL10n: null,
    projectId: 7,
    sheetCount: 1,
    sheetStates: { S1: state },
  });

  render(
    <MantineProvider>
      <QueryClientProvider client={client}>
        <DocumentActionBar
          documentId={1}
          periodKey={Feb}
          sheetDefId={42}
          sheetCode="S1"
          sheetName="Water"
          state={state}
          lock={null}
          readOnly={false}
          language="en"
          canImport={false}
          canExport={false}
          validate={{ loading: false, run: () => undefined }}
          documentItems={[]}
        />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('DocumentActionBar: «Подати: <період>» (D-441)', () => {
  it('у лютому з неподаним січнем кнопка подає СІЧЕНЬ і попереджає «наступний — лютий»', async () => {
    show();

    const submit = await screen.findByRole('button', { name: /workflow\.submitPeriod/ });
    expect(submit.getAttribute('data-period-key')).toBe(String(Jan));
    expect(submit.getAttribute('data-variant')).toBe('filled');
    expect(screen.getByTestId('submit-next-period').textContent).toContain('workflow.submitNextPeriod');

    fireEvent.click(submit);

    await waitFor(() => expect(submits).toHaveLength(1));
    expect(submits[0]).toMatchObject({ sheetDefId: 42, periodKey: Jan });
  });

  it('▾ дає вибрати інший відкритий період — кнопка подає його', async () => {
    show();

    const submit = await screen.findByRole('button', { name: /workflow\.submitPeriod/ });
    fireEvent.click(screen.getByRole('button', { name: '⟦workflow.pickSubmitPeriod⟧' }));
    const february = await waitFor(() => {
      const item = document.querySelector<HTMLElement>(`[data-submit-period="${String(Feb)}"]`);
      expect(item).not.toBeNull();

      return item as HTMLElement;
    });
    fireEvent.click(february);

    await waitFor(() => expect(submit.getAttribute('data-period-key')).toBe(String(Feb)));
    // Лютий — останній неподаний: попередження «наступний» більше немає.
    expect(screen.queryByTestId('submit-next-period')).toBeNull();

    fireEvent.click(submit);
    await waitFor(() => expect(submits).toHaveLength(1));
    expect(submits[0]).toMatchObject({ periodKey: Feb });
  });

  it('поданий січень: ціль — лютий, вибору немає (один неподаний період)', async () => {
    show('Draft', 'Submitted');

    const submit = await screen.findByRole('button', { name: /workflow\.submitPeriod/ });
    await waitFor(() => expect(submit.getAttribute('data-period-key')).toBe(String(Feb)));
    expect(screen.queryByRole('button', { name: '⟦workflow.pickSubmitPeriod⟧' })).toBeNull();
    expect(screen.queryByTestId('submit-next-period')).toBeNull();
  });

  it('відкритий аркуш поданий, а січень ні: головна — «Затвердити», подання січня — другорядною кнопкою', async () => {
    show('Submitted');

    const approve = await screen.findByRole('button', { name: '⟦workflow.approve⟧' });
    const submit = await screen.findByRole('button', { name: /workflow\.submitPeriod/ });

    expect(approve.getAttribute('data-variant')).toBe('filled');
    expect(submit.getAttribute('data-variant')).toBe('default');
    expect(submit.getAttribute('data-period-key')).toBe(String(Jan));
  });
});

describe('markedMonthColumns: позначаються лише подані й затверджені колонки своєї таблиці (D-441)', () => {
  it('Submitted/Approved — так; Draft/Rejected і чужа таблиця — ні', () => {
    const marked = markedMonthColumns(
      [
        { tableDefId: 5, columnCode: 'JAN', monthNumber: 1, periodKey: Jan, sheetStatus: 'Submitted' },
        { tableDefId: 5, columnCode: 'FEB', monthNumber: 2, periodKey: Feb, sheetStatus: 'Approved' },
        { tableDefId: 5, columnCode: 'MAR', monthNumber: 3, periodKey: 202603, sheetStatus: 'Draft' },
        { tableDefId: 5, columnCode: 'APR', monthNumber: 4, periodKey: 202604, sheetStatus: 'Rejected' },
        { tableDefId: 6, columnCode: 'JAN', monthNumber: 1, periodKey: Jan, sheetStatus: 'Approved' },
      ],
      5,
    );

    expect([...marked.entries()]).toEqual([
      ['JAN', 'Submitted'],
      ['FEB', 'Approved'],
    ]);
    expect(markedMonthColumns(undefined, 5).size).toBe(0);
  });
});
