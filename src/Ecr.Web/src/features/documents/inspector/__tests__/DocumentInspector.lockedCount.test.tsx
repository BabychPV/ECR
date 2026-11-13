import type { JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, render, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import type { DocumentTableDto, TableSliceDto } from '@/api/types';
import { tableAttentionOf } from '@/features/grid/rowAttention';
import { publishTableAttention, requestAttentionFocus, resetRowAttentionForTests } from '@/features/grid/rowAttentionStore';
import { testTheme } from '@/test/render';
import { DocumentInspector } from '../DocumentInspector';

/**
 * B51 (2), F13 RC #849: «ROWS THAT NEED ATTENTION 0» above five listed locked rows. The section header counts the
 * rows the section is about — locked rows included (distinct rows: a row both orphaned and locked is one row).
 *
 * ⛔ Red without the fix: the header counted only rows with a lost reference / permit warning — «0».
 */

const Tables: DocumentTableDto[] = [
  {
    allowsDynamicRows: true,
    copyOrdinal: 1,
    isCopyable: false,
    maxDynamicRows: null,
    sheetCode: 'T3',
    sheetDefId: 31,
    sheetNameL10n: { values: { en: 'Stationary equipment' } },
    sheetOrdinal: 0,
    tableCode: 'T3_00',
    tableDefId: 300,
    tableInstanceId: 960,
    tableNameL10n: { values: { en: '3.00 Stationary Equipment' } },
    tableOrdinal: 0,
  } as DocumentTableDto,
];

const Slice = {
  tableInstanceId: 960,
  periodKey: 202601,
  columns: [{ code: 'RH_1', header: 'Running hours per month (hour)', dataType: 'Decimal' }],
  rows: [
    { rowKey: 'A', label: 'One', cells: {}, isOrphaned: false, ordinal: 1, rowKind: 'Item', rowVersion: 'AA' },
    { rowKey: 'B', label: 'Two', cells: {}, isOrphaned: true, ordinal: 2, rowKind: 'Item', rowVersion: 'AB' },
    { rowKey: 'C', label: 'Three', cells: {}, isOrphaned: false, ordinal: 3, rowKind: 'Item', rowVersion: 'AC' },
  ],
  cellPermissions: { 'A:RH_1': 'PeriodClosed', 'B:RH_1': 'PeriodClosed', 'C:RH_1': 'PeriodClosed' },
  cellConfirmations: {},
} as unknown as TableSliceDto;

function Harness(): JSX.Element {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  return (
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={['/documents/96']}>
          <DocumentInspector documentId={96} periodKey={202601} tables={Tables} messages={[]} onSelectFinding={() => undefined} />
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>
  );
}

afterEach(() => {
  cleanup();
  act(() => resetRowAttentionForTests());
  vi.unstubAllGlobals();
});

describe('B51 (2): the «Rows that need attention» header counts locked rows', () => {
  it('three locked rows, one of them also orphaned — header says 3, not 1 (or 0)', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => new Response('[]', { status: 200, headers: { 'Content-Type': 'application/json' } })));
    render(<Harness />);
    act(() => publishTableAttention(960, tableAttentionOf(Slice, undefined, undefined, 300)));
    act(() => requestAttentionFocus('960:A:locked'));

    await waitFor(() => expect(document.querySelectorAll('[data-inspector-locked-row]')).toHaveLength(3));
    expect(document.querySelector('[data-inspector-attention-count]')?.textContent).toBe('3');
  });
});
