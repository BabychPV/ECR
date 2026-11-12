import type { JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import type { DocumentTableDto, TableSliceDto, ValidationFindingDto } from '@/api/types';
import { lockedRowsOf, tableAttentionOf, type ColumnMonth } from '@/features/grid/rowAttention';
import { publishTableAttention, requestAttentionFocus, resetRowAttentionForTests } from '@/features/grid/rowAttentionStore';
import { testTheme } from '@/test/render';
import { DocumentInspector } from '../DocumentInspector';
import { clearInspectedCell } from '../inspectedCell';

/**
 * ✎ 12.11 (людина, doc 96 жовтень 2026, T3_00): пункт закритих місяців у «Rows that need attention».
 *
 * Було одне речення на таблицю з сирими заголовками колонок («Running hours per month (hour)–Gas consumption (Sm3),
 * January. RH during exceeding–0337 CO (g/s), April. … locked — values there are read-only. Reopen the month to edit.»),
 * а клік нікуди не вів. Тепер: одне коротке речення на РЯДОК (код і назва рядка, місяці назвами без повторів і за
 * порядком; колонки — лише в розгортанні), клік веде в рядок тим самим шляхом, що й пункт перевірки, а «Reopen …» —
 * лише тому, кому сервер дозволяє (`canOpen`); без права — речення, хто може.
 *
 * ⛔ Червоне без фіксу: немає `lockedRowsOf`/`lockedRows` — пункт таблиці з `inspector.attention.lockedMonths` і
 * сирими заголовками; немає `[data-inspector-locked-row]`, кліку й дії.
 */

const Tables: DocumentTableDto[] = [
  {
    allowsDynamicRows: false,
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

function column(code: string, header: string): TableSliceDto['columns'][number] {
  return { code, header, dataType: 'Decimal' } as TableSliceDto['columns'][number];
}

/** Рядок LTD-A01: закриті комірки січня (дві колонки), квітня й липня; жовтень відкритий. */
const Slice = {
  tableInstanceId: 960,
  periodKey: 202610,
  columns: [
    column('RH_1', 'Running hours per month (hour)'),
    column('GAS_1', 'Gas consumption (Sm3)'),
    column('RH_4', 'RH during exceeding'),
    column('RH_7', 'RH during exceeding'),
    column('RH_10', 'Running hours per month (hour)'),
  ],
  rows: [
    { rowKey: 'LTD-A01', label: 'Gasoline blow torch', cells: {}, isOrphaned: false, ordinal: 1, rowKind: 'Item', rowVersion: 'AA' },
    { rowKey: 'LTD-A02', label: 'LTD-A02', cells: {}, isOrphaned: false, ordinal: 2, rowKind: 'Item', rowVersion: 'AB' },
    { rowKey: 'LTD-A03', label: 'Open row', cells: {}, isOrphaned: false, ordinal: 3, rowKind: 'Item', rowVersion: 'AC' },
  ],
  cellPermissions: {
    'LTD-A01:RH_7': 'PeriodClosed',
    'LTD-A01:RH_1': 'PeriodClosed',
    'LTD-A01:GAS_1': 'PeriodClosed',
    'LTD-A01:RH_4': 'PeriodClosed',
    'LTD-A02:RH_4': 'PeriodClosed',
    'LTD-A03:RH_10': 'CalculatedCell',
  },
  cellConfirmations: {},
} as unknown as TableSliceDto;

const Months: ReadonlyMap<string, ColumnMonth> = new Map([
  ['RH_1', { monthNumber: 1, periodKey: 202601 }],
  ['GAS_1', { monthNumber: 1, periodKey: 202601 }],
  ['RH_4', { monthNumber: 4, periodKey: 202604 }],
  ['RH_7', { monthNumber: 7, periodKey: 202607 }],
  ['RH_10', { monthNumber: 10, periodKey: 202610 }],
]);

const json = (body: unknown): Response =>
  new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });

/** `GET …/period-reopen` — `canOpen` за параметром; решта запитів — порожньо. */
function stubReopen(canOpen: boolean): ReturnType<typeof vi.fn> {
  const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
    const url = String(input);
    const match = /period-reopen\?periodKey=(\d+)/.exec(url);
    if (match !== null) {
      return json({
        periodKey: Number(match[1]),
        active: null,
        canOpen,
        denial: canOpen ? null : 'NoPermission',
        defaultOpenUntil: '2026-11-12T21:00:00Z',
      });
    }
    return json([]);
  });
  vi.stubGlobal('fetch', fetchMock);

  return fetchMock;
}

function Harness({ onSelect }: { onSelect: (finding: ValidationFindingDto) => void }): JSX.Element {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  return (
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={['/documents/96']}>
          <DocumentInspector documentId={96} periodKey={202610} tables={Tables} messages={[]} onSelectFinding={onSelect} />
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>
  );
}

async function openOn(key: string): Promise<HTMLElement> {
  act(() => publishTableAttention(960, tableAttentionOf(Slice, undefined, undefined, 300, Months)));
  act(() => requestAttentionFocus(key));
  await waitFor(() => expect(document.querySelector(`[data-inspector-locked-row="${key}"]`)).not.toBeNull());

  return document.querySelector<HTMLElement>(`[data-inspector-locked-row="${key}"]`) as HTMLElement;
}

afterEach(() => {
  cleanup();
  act(() => resetRowAttentionForTests());
  act(() => clearInspectedCell());
  vi.unstubAllGlobals();
});

describe('lockedRowsOf: закриті місяці по рядку', () => {
  it('рядок — з кодом і назвою, місяці без повторів і за порядком, перша закрита колонка — адреса кліку', () => {
    const rows = lockedRowsOf(Slice, Months);

    expect(rows.map((row) => row.rowKey)).toEqual(['LTD-A01', 'LTD-A02']);
    const first = rows[0];
    expect(first?.rowName).toBe('Gasoline blow torch');
    expect(first?.months).toEqual([1, 4, 7]);
    expect(first?.periodKeys).toEqual([202601, 202604, 202607]);
    expect(first?.columnCode).toBe('RH_1');
    // Колонки — тим самим описом, що й раніше (серія з трьох і більше — діапазоном).
    expect(first?.columns).toBe('Running hours per month (hour)–RH during exceeding');
    // Назва, що збігається з кодом, — не назва.
    expect(rows[1]?.rowName).toBeNull();
  });

  it('колонка без місяця — місяць самого зрізу', () => {
    expect(lockedRowsOf(Slice)[0]?.months).toEqual([10]);
    expect(lockedRowsOf(Slice)[0]?.periodKeys).toEqual([202610]);
  });
});

describe('Issues: пункт закритих місяців (12.11)', () => {
  it('одне речення на рядок: код і назва рядка, місяці назвами; заголовки колонок — лише в розгортанні', async () => {
    stubReopen(false);
    render(<Harness onSelect={() => undefined} />);
    const item = await openOn('960:LTD-A01:locked');

    const text = item.textContent ?? '';
    expect(text).toContain('inspector.attention.lockedRowMany');
    expect(text).toContain('row=LTD-A01 (Gasoline blow torch)');
    expect(text).toMatch(/January, April/);
    expect(text).toContain('July');
    expect(text).not.toContain('Running hours per month');
    expect(text).not.toContain('inspector.attention.lockedMonths');
    // Таблицевого речення з сирими заголовками більше немає.
    expect(document.querySelector('[data-inspector-locked="960"]')).toBeNull();

    const details = document.querySelector('[data-inspector-locked-columns="960:LTD-A01:locked"]');
    expect(details?.textContent).toContain('inspector.attention.lockedColumns');
    expect(details?.textContent).toContain('Running hours per month (hour)');
  });

  it('один місяць — речення «закритий місяць»', async () => {
    stubReopen(false);
    render(<Harness onSelect={() => undefined} />);
    const item = await openOn('960:LTD-A02:locked');

    expect(item.textContent).toContain('inspector.attention.lockedRowOne');
    expect(item.textContent).toContain('row=LTD-A02,');
    expect(item.textContent).toContain('months=April');
  });

  it('клік веде в рядок тим самим шляхом, що й пункт перевірки (таблиця, рядок, перша закрита колонка)', async () => {
    stubReopen(false);
    const onSelect = vi.fn();
    render(<Harness onSelect={onSelect} />);
    const item = await openOn('960:LTD-A01:locked');

    fireEvent.click(item);

    expect(onSelect).toHaveBeenCalledTimes(1);
    expect(onSelect.mock.calls[0]?.[0]).toMatchObject({ tableDefId: 300, tableInstanceId: 960, rowKey: 'LTD-A01', columnCode: 'RH_1' });
  });

  it('з правом відкриття — «Reopen <місяць> for this document…» на кожен закритий місяць; без права — хто може', async () => {
    stubReopen(true);
    render(<Harness onSelect={() => undefined} />);
    await openOn('960:LTD-A01:locked');

    await waitFor(() => expect(document.querySelectorAll('[data-inspector-reopen]')).toHaveLength(3));
    expect(
      Array.from(document.querySelectorAll('[data-inspector-reopen]')).map((button) => button.getAttribute('data-inspector-reopen')),
    ).toEqual(['202601', '202604', '202607']);
    expect(document.querySelector('[data-inspector-reopen]')?.textContent).toContain('document.reopen.action');
    expect(document.querySelector('[data-inspector-reopen-denied]')).toBeNull();

    cleanup();
    act(() => resetRowAttentionForTests());
    stubReopen(false);
    render(<Harness onSelect={() => undefined} />);
    await openOn('960:LTD-A01:locked');

    await waitFor(() => expect(document.querySelector('[data-inspector-reopen-denied]')).not.toBeNull());
    expect(document.querySelector('[data-inspector-reopen-denied]')?.textContent).toContain('document.reopen.noPermission');
    expect(document.querySelector('[data-inspector-reopen]')).toBeNull();
  });
});
