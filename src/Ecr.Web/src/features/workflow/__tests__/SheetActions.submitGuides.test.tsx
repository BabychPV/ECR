import type { JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { clearCellNavigation, currentCellNavigation } from '@/features/grid/cellNavigation';
import { needsInputSummary, publishNeedsInput, publishUnmountedTables, resetNeedsInput, useNeedsInputEmphasis } from '@/features/grid/needsInputStore';
import { guardSubmitNeedsInput } from '../submitGuide';
import { NoticeLine } from '@/features/documents/notices/NoticeLine';
import { resetNotices } from '@/features/documents/notices/noticeStore';
import { SheetActions } from '../SheetActions';

/**
 * ✎ DOCUX L9 (AC L9-1, рішення людини Q6): «Submit» із порожніми обов'язковими комірками — запиту немає й діалогу немає,
 * фокус (перехід) — на першу порожню, рамка 2 px, у рядку повідомлень «Cannot submit: N required cells empty · Go to next
 * (F8)»; заповнили все — повідомлення зникло, «Submit» знову питає підтвердження.
 */
const CurrentUser = {
  denies: [],
  grants: { 'Project:7': 'Submit' },
  isSimulation: false,
  language: 'en',
  mustChangePassword: false,
  permissions: [],
  simulatedForUserId: null,
  userId: 9,
  userName: 'author',
};

const json = (body: unknown): Response =>
  new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });

function Emphasis(): JSX.Element {
  return <output data-testid="emphasis">{String(useNeedsInputEmphasis())}</output>;
}

function show(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => (String(input).includes('/api/v1/me') ? json(CurrentUser) : json(null))),
  );
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  client.setQueryData(['document', 1, 202601], { businessKey: 'DOC-1', createdAt: '2026-01-01T00:00:00Z', id: 1, nameL10n: null, projectId: 7, sheetCount: 1, sheetStates: { S1: 'Draft' } });
  render(
    <MantineProvider>
      <MemoryRouter>
        <QueryClientProvider client={client}>
          <NoticeLine />
          <SheetActions documentId={1} sheetDefId={42} periodKey={202601} state="Draft" />
          <Emphasis />
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

function needs(count: number): void {
  publishNeedsInput({
    tableInstanceId: 5,
    tableDefId: 50,
    periodKey: 202601,
    order: 5,
    labelOffset: 0,
    cells: Array.from({ length: count }, (_, index) => ({
      rowKey: `r${String(index + 1)}`,
      columnCode: 'NAME',
      level: 'block' as const,
      source: 'template' as const,
      methodologies: [],
      rowIndex: index,
      columnIndex: 0,
    })),
  });
}

afterEach(() => {
  vi.unstubAllGlobals();
  resetNeedsInput();
  resetNotices();
  clearCellNavigation();
});

describe('SheetActions: Submit веде до порожніх обов’язкових комірок (DOCUX L9)', () => {
  it('L9-1: порожні є — ні діалогу, ні запиту; перехід до першої, рамка 2 px, рядок «Cannot submit: 3…»; заповнили — зникло', async () => {
    show();
    act(() => needs(3));

    fireEvent.click(await screen.findByRole('button', { name: /workflow\.submitPeriod/ }));

    expect(screen.queryByRole('dialog')).toBeNull();
    expect(vi.mocked(fetch).mock.calls.filter((call) => String(call[0]).includes('/submit'))).toHaveLength(0);
    expect(currentCellNavigation()).toMatchObject({ tableInstanceId: 5, rowKey: 'r1', columnCode: 'NAME' });
    expect(screen.getByTestId('emphasis').textContent).toBe('true');
    const line = await screen.findByTestId('notice-cannot-submit');
    expect(line.textContent).toContain('document.notices.cannotSubmitRequired (count=3)');
    expect(screen.getByTestId('notice-cannot-submit-next').getAttribute('aria-keyshortcuts')).toBe('F8');

    act(() => needs(1));
    expect((await screen.findByTestId('notice-cannot-submit')).textContent).toContain('(count=1)');

    act(() => needs(0));
    await waitFor(() => expect(screen.queryByTestId('notice-cannot-submit')).toBeNull());
    expect(screen.getByTestId('emphasis').textContent).toBe('false');

    fireEvent.click(screen.getByRole('button', { name: /workflow\.submitPeriod/ }));
    expect(await screen.findByRole('dialog')).toBeTruthy();
  });
});

describe('Submit guidance: річна розкладка й незмонтовані таблиці (доручення координатора)', () => {
  function yearCells(): void {
    const cell = (rowKey: string, columnCode: string, columnIndex: number, ownerPeriodKey: number | null) => ({
      rowKey,
      columnCode,
      level: 'block' as const,
      source: 'template' as const,
      methodologies: [],
      rowIndex: 0,
      columnIndex,
      ownerPeriodKey,
    });
    publishNeedsInput({
      tableInstanceId: 9,
      tableDefId: 90,
      periodKey: 202601,
      order: 9,
      labelOffset: 0,
      cells: [cell('r1', 'NAME', 0, null), cell('r1', 'M04', 4, 202604), cell('r1', 'M05', 5, 202605)],
    });
  }

  it('«Submit: квітень» рахує лише квітень і колонки без місяця; травень не заважає; без фільтра — усе', () => {
    yearCells();
    expect(needsInputSummary(202604).count).toBe(2);
    expect(needsInputSummary(202606).count).toBe(1);
    expect(needsInputSummary().count).toBe(3);

    expect(guardSubmitNeedsInput(202604)).toBe(true);
    expect(currentCellNavigation()).toMatchObject({ rowKey: 'r1', columnCode: 'NAME' });
  });

  it('таблиці, ще не відкриті прокруткою, — названо в рядку «Cannot submit…», а не мовчки', async () => {
    show();
    act(() => {
      needs(1);
      publishUnmountedTables(3);
    });
    fireEvent.click(await screen.findByRole('button', { name: /workflow\.submitPeriod/ }));
    const line = await screen.findByTestId('notice-cannot-submit');
    expect(line.textContent).toContain('grid.needsInput.otherTables (count=3)');
  });
});

