import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { queryKeys } from '@/api/queryKeys';
import type { DocumentTableDto } from '@/api/types';
import { testTheme } from '@/test/render';
import { permitHistoryKey, type DocumentPermitHistory } from '../api';
import { HeaderPermitSection } from '../HeaderPermitSection';
import { HeaderSplitBanner } from '../HeaderSplitBanner';
import { headerSplitLinesOf, pendingRowsByTable } from '../headerSplitModel';
import { seaWaterRow } from './historyFixture';

/**
 * Feature (j) Ship 2 (mockup jh1–jh3): a mid-month HEADER permit change.
 *  - jh1: the header field's history and «+ Add permit from date…» (the add goes to the header endpoint with the period);
 *  - jh2: the banner names the month, the count of rows and the tables; rows listed on «Show rows»;
 *  - jh3: «Split all…» — one line per row, «was X», partial save sends only the typed rows.
 */
vi.mock('@/shared/i18n', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/i18n')>()),
  t: (key: string, params?: Record<string, string | number>) =>
    params === undefined ? key : `${key} ${JSON.stringify(params)}`,
}));

const saveCellSplits = vi.fn();
const addHeaderPermit = vi.fn();
vi.mock('@/features/permits/history/api', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/features/permits/history/api')>()),
  saveCellSplits: (...args: unknown[]) => saveCellSplits(...args) as unknown,
  addHeaderPermit: (...args: unknown[]) => addHeaderPermit(...args) as unknown,
  getPermitHistory: () => new Promise(() => undefined),
}));

afterEach(() => {
  cleanup();
  saveCellSplits.mockReset();
  addHeaderPermit.mockReset();
});

/** A row of «Air emissions» relying on the header permit; February holds `total`, not split. */
function airRow(rowKey: string, total: string | null, extra: Partial<ReturnType<typeof seaWaterRow>> = {}) {
  const base = seaWaterRow();
  const month = base.splitMonths[0]!;
  return seaWaterRow({
    tableDefId: 801,
    tableCode: 'AIR',
    rowKey,
    columnCode: 'PermitNo',
    permitSource: 'header',
    splitMonths: [{ ...month, columnCodes: total === null ? [] : ['Feb'], cells: [{ columnCode: 'Feb', total, parts: [null, null], isSplit: false }] }],
    ...extra,
  });
}

const history: DocumentPermitHistory = {
  header: [{ fieldCode: 'PermitNo', permits: seaWaterRow().permits }],
  rows: [
    // The server lists the rows newest first (CI 42684887329: «row 2» before «row 1»): the UI orders them like the table.
    airRow('SO2', '7.3'),
    airRow('NO2', '26.7'),
    // A calculated row (server lists nothing to split) — never a line (F13 p220 j7).
    airRow('SUM', null, { splitMonths: [{ ...seaWaterRow().splitMonths[0]!, columnCodes: [], cells: [{ columnCode: 'Feb', total: '34', parts: [null, null], isSplit: false }] }] }),
    // A row with its own permit column — not the header's business.
    seaWaterRow(),
  ],
  cellsNeedingSplit: 3,
};

const tables = [
  { tableDefId: 801, sheetCopyId: null, tableInstanceId: 51, tableCode: 'AIR', tableNameL10n: { values: { en: 'Air emissions' } } },
  { tableDefId: 701, sheetCopyId: null, tableInstanceId: 41, tableCode: 'WATER', tableNameL10n: { values: { en: 'Water Report' } } },
] as unknown as DocumentTableDto[];

const airSlice = {
  tableInstanceId: 51,
  periodKey: 202602,
  columns: [{ code: 'Feb', dataType: 'Decimal', unitSymbol: 't' }],
  rows: [
    { rowKey: 'NO2', label: 'Nitrogen dioxide', rowVersion: 'V1', cells: {} },
    { rowKey: 'SO2', label: 'Sulphur dioxide', rowVersion: 'V2', cells: {} },
  ],
};

function client(): QueryClient {
  const queries = new QueryClient({ defaultOptions: { queries: { retry: false, staleTime: Infinity } } });
  queries.setQueryData(permitHistoryKey(3, 202602), history);
  queries.setQueryData(queryKeys.slices.one(51, 202602), airSlice);
  return queries;
}

function wrap(node: React.ReactNode, queries = client()): void {
  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={queries}>{node}</QueryClientProvider>
    </MantineProvider>,
  );
}

describe('header split model', () => {
  it('lines: only rows relying on the header and cells the server lists; counts by table', () => {
    const lines = headerSplitLinesOf(history, tables);
    expect(lines.map((line) => `${line.row.rowKey}:${line.columnCode}`)).toEqual(['SO2:Feb', 'NO2:Feb']);
    expect(lines[0]?.table?.tableInstanceId).toBe(51);
    expect(pendingRowsByTable(lines).map((entry) => [entry.tableDefId, entry.rows])).toEqual([[801, 2]]);
  });
});

describe('HeaderSplitBanner (jh2/jh3)', () => {
  it('jh2: names the month, 2 rows, the change date, both permits and the table; the rows are listed (≤ 3: all)', () => {
    wrap(<HeaderSplitBanner documentId={3} periodKey={202602} tables={tables} readOnly={false} />);
    const banner = document.querySelector('[data-header-split-banner]') as HTMLElement;

    expect(banner.textContent).toContain('permits.header.bannerTitle');
    expect(banner.textContent).toContain('"count":2');
    expect(banner.textContent).toContain('"date":"15.02.2026"');
    expect(banner.textContent).toContain('"old":"KZ29VCZ14622425"');
    const rows = [...banner.querySelectorAll('[data-header-split-rows] li')].map((item) => item.textContent);
    expect(rows).toEqual(['Air emissions — Nitrogen dioxide', 'Air emissions — Sulphur dioxide']);
  });

  it('F13 p223 (2): compact — collapsed shows the summary, the first 3 rows and «Show all N»; chips only when expanded', () => {
    const queries = client();
    const many = ['R1', 'R2', 'R3', 'R4', 'R5'].map((key) => airRow(key, '1'));
    queries.setQueryData(permitHistoryKey(3, 202602), { ...history, rows: many });
    wrap(<HeaderSplitBanner documentId={3} periodKey={202602} tables={tables} readOnly={false} />, queries);
    const banner = document.querySelector('[data-header-split-banner]') as HTMLElement;

    expect(banner.querySelectorAll('[data-header-split-rows] li')).toHaveLength(3);
    expect(banner.textContent).not.toContain('permits.header.bannerTable');
    fireEvent.click(within(banner).getByRole('button', { name: 'permits.header.showAll {"count":5}' }));
    expect(banner.querySelectorAll('[data-header-split-rows] li')).toHaveLength(5);
    expect(banner.textContent).toContain('"table":"Air emissions","count":5');
  });

  it('F13 p223 (3): the banner and «Split all» count the same rows (rows still to split), not cells', () => {
    const queries = client();
    const month = seaWaterRow().splitMonths[0]!;
    // NO2 has two month cells to split, SO2 is already split: 1 row to split, not «2 cells» / «of 3».
    queries.setQueryData(permitHistoryKey(3, 202602), {
      ...history,
      rows: [
        airRow('NO2', '26.7', { splitMonths: [{ ...month, columnCodes: ['Feb', 'FebB'], cells: [
          { columnCode: 'Feb', total: '26.7', parts: [null, null], isSplit: false },
          { columnCode: 'FebB', total: '3', parts: [null, null], isSplit: false },
        ] }] }),
        airRow('SO2', null, { splitMonths: [{ ...month, columnCodes: [], cells: [{ columnCode: 'Feb', total: '7.3', parts: ['3', '4.3'], isSplit: true }] }] }),
      ],
    });
    wrap(<HeaderSplitBanner documentId={3} periodKey={202602} tables={tables} readOnly={false} />, queries);
    const banner = document.querySelector('[data-header-split-banner]') as HTMLElement;
    expect(banner.textContent).toContain('"count":1');

    fireEvent.click(screen.getByRole('button', { name: 'permits.header.splitAll' }));
    const dialog = screen.getByRole('dialog');
    expect(dialog.textContent).toContain('permits.header.dialogTitle');
    expect(dialog.textContent).toContain('"count":1}');
    expect(dialog.textContent).toContain('permits.header.progress {"done":0,"count":1}');
  });

  it('nothing relying on the header to split — no banner', () => {
    const queries = client();
    queries.setQueryData(permitHistoryKey(3, 202602), { ...history, rows: [seaWaterRow()] });
    wrap(<HeaderSplitBanner documentId={3} periodKey={202602} tables={tables} readOnly={false} />, queries);

    expect(document.querySelector('[data-header-split-banner]')).toBeNull();
  });

  it('jh3: Split all — a line per row with «was X»; partial save sends only the typed row with its version', async () => {
    saveCellSplits.mockResolvedValue(history);
    wrap(<HeaderSplitBanner documentId={3} periodKey={202602} tables={tables} readOnly={false} />);
    fireEvent.click(screen.getByRole('button', { name: 'permits.header.splitAll' }));
    const dialog = screen.getByRole('dialog');

    expect([...dialog.querySelectorAll('[data-header-split-line]')].map((line) => line.getAttribute('data-header-split-line'))).toEqual(['NO2', 'SO2']);
    expect(dialog.textContent).toContain('permits.header.was {"value":"26.7"}');
    expect(dialog.textContent).toContain('permits.header.progress {"done":0,"count":2}');

    fireEvent.change(within(dialog).getByLabelText(/^Nitrogen dioxide · permits\.split\.part \{"part":"A"\}/), { target: { value: '12.6' } });
    fireEvent.change(within(dialog).getByLabelText(/^Nitrogen dioxide · permits\.split\.part \{"part":"B"\}/), { target: { value: '14.1' } });
    expect(dialog.textContent).toContain('26.7');
    expect(dialog.textContent).toContain('permits.header.progress {"done":1,"count":2}');

    fireEvent.click(within(dialog).getByRole('button', { name: 'permits.split.save' }));
    await waitFor(() => expect(saveCellSplits).toHaveBeenCalled());
    expect(saveCellSplits).toHaveBeenCalledWith(3, {
      periodKey: 202602,
      items: [{ tableInstanceId: 51, rowKey: 'NO2', columnCode: 'Feb', baseVersion: 'V1', parts: ['12.6', '14.1'] }],
    });
  });
});

describe('HeaderPermitSection (jh1)', () => {
  it('F13 p223 (4): a change on the 1st — the note after saving does not ask to split anything', async () => {
    const queries = client();
    queries.setQueryData([...queryKeys.registries.entries('PRM_WATER'), 'document', 3], [
      { id: 11, code: 'E1', display: 'KZ29VCZ14622425', validFrom: '2021-02-15', validTo: '2026-01-31' },
      { id: 12, code: 'E2', display: 'KZ46VCZ14622410', validFrom: '2026-02-01', validTo: '2031-02-14' },
    ]);
    addHeaderPermit.mockResolvedValue({ fieldCode: 'PermitNo', permits: [] });
    wrap(
      <HeaderPermitSection documentId={3} periodKey={202602} fieldCode="PermitNo" registryCode="PRM_WATER" currentEntryId={11} canEdit />,
      queries,
    );
    fireEvent.click(screen.getByRole('button', { name: '+ permits.history.add' }));
    const form = screen.getByRole('form', { name: 'permits.header.addTitle' });
    fireEvent.click(within(form).getByRole('textbox', { name: 'permits.history.permit' }));
    fireEvent.click(await screen.findByRole('option', { name: /KZ46VCZ14622410/ }));
    expect(form.querySelector('[data-add-split-hint]')).toBeNull();
    fireEvent.click(within(form).getByRole('button', { name: 'permits.history.addSubmit' }));

    const note = await waitFor(() => {
      const found = document.querySelector('[data-header-permit-added]');
      expect(found).not.toBeNull();
      return found as HTMLElement;
    });
    expect(addHeaderPermit).toHaveBeenCalledWith(3, expect.objectContaining({ validFrom: '2026-02-01' }));
    expect(note.textContent).toContain('permits.header.addedWhole');
    expect(note.textContent).not.toContain('permits.header.added ');
  });

  it('shows the header history and adds a permit from a date to the header with the period', async () => {
    const queries = client();
    queries.setQueryData([...queryKeys.registries.entries('PRM_WATER'), 'document', 3], [
      { id: 11, code: 'E1', display: 'KZ29VCZ14622425', validFrom: '2021-02-15', validTo: '2026-02-14' },
      { id: 12, code: 'E2', display: 'KZ46VCZ14622410', validFrom: '2026-02-15', validTo: '2031-02-14' },
    ]);
    addHeaderPermit.mockResolvedValue({ fieldCode: 'PermitNo', permits: [] });
    wrap(
      <HeaderPermitSection documentId={3} periodKey={202602} fieldCode="PermitNo" registryCode="PRM_WATER" currentEntryId={11} canEdit />,
      queries,
    );

    const list = screen.getByRole('list', { name: 'permits.header.historyTitle' });
    expect(list.textContent).toContain('KZ29VCZ14622425');
    expect(list.textContent).toContain('permits.cell.from {"date":"15.02.2026"}');
    expect(screen.getByRole('button', { name: 'permits.history.undo' })).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: '+ permits.history.add' }));
    const form = screen.getByRole('form', { name: 'permits.header.addTitle' });
    await waitFor(() => expect(form.textContent).toContain('permits.header.validFrom'));
    fireEvent.click(within(form).getByRole('textbox', { name: 'permits.history.permit' }));
    fireEvent.click(await screen.findByRole('option', { name: /KZ46VCZ14622410/ }));
    fireEvent.click(within(form).getByRole('button', { name: 'permits.history.addSubmit' }));

    await waitFor(() => expect(addHeaderPermit).toHaveBeenCalled());
    expect(addHeaderPermit).toHaveBeenCalledWith(3, { fieldCode: 'PermitNo', registryEntryId: 12, validFrom: '2026-02-15', periodKey: 202602 });
  });
});
