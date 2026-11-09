import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { EcrApiError } from '@/api/client';
import { queryKeys } from '@/api/queryKeys';
import { testTheme } from '@/test/render';
import { permitHistoryKey } from '../api';
import { RowPermitSection, type RowPermitCell } from '../RowPermitSection';
import { cellSplitRequest, closeCellSplit } from '../splitRequest';
import { historyOf, seaWaterRow } from './historyFixture';

/**
 * Feature (j) Inspector «Info»: j1b history of the permit cell with «Undo last», j1a «Add permit from date…» (default
 * date = the day after the current permit ends), j4 gap refusal with «move the start date» re-submitting the suggested
 * date, j2 «Edit parts» opening the split dialog, and the note on calculated cells.
 */
vi.mock('@/shared/i18n', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/i18n')>()),
  t: (key: string, params?: Record<string, string | number>) =>
    params === undefined ? key : `${key} ${JSON.stringify(params)}`,
}));

const addRowPermit = vi.fn();
const undoRowPermit = vi.fn();
vi.mock('@/features/permits/history/api', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/features/permits/history/api')>()),
  addRowPermit: (...args: unknown[]) => addRowPermit(...args) as unknown,
  undoRowPermit: (...args: unknown[]) => undoRowPermit(...args) as unknown,
  getPermitHistory: () => new Promise(() => undefined),
}));

afterEach(() => {
  cleanup();
  addRowPermit.mockReset();
  undoRowPermit.mockReset();
  closeCellSplit();
});

const slice = {
  tableInstanceId: 41,
  periodKey: 202602,
  columns: [
    { code: 'Permit', dataType: 'Lookup', lookupRegistryDefId: 90, header: 'Permit', unitSymbol: null },
    { code: 'Feb', dataType: 'Decimal', lookupRegistryDefId: null, header: 'Feb', unitSymbol: 'thousand m³' },
  ],
  rows: [{ rowKey: 'R1', label: 'Sea Water Intake', rowVersion: 'AAAB', cells: {} }],
};

const entries = [
  { id: 11, code: 'KZ29VCZ14622425', display: 'KZ29VCZ14622425', validFrom: '2021-02-15', validTo: '2026-02-14' },
  { id: 12, code: 'KZ46VCZ14622410', display: 'KZ46VCZ14622410', validFrom: '2026-02-15', validTo: '2031-02-14' },
];

function show(cell: Partial<RowPermitCell>, rows = [seaWaterRow()]): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false, staleTime: Infinity } } });
  client.setQueryData(queryKeys.slices.one(41, 202602), slice);
  client.setQueryData(queryKeys.registries.lookupTargets(), [{ id: 90, code: 'PRM_WATER', kind: 'PermitType', nameL10n: {} }]);
  client.setQueryData([...queryKeys.registries.entries('PRM_WATER'), 'document', 3], entries);
  client.setQueryData(permitHistoryKey(3, 202602), historyOf(...rows));
  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <RowPermitSection
          documentId={3}
          periodKey={202602}
          tableDefId={701}
          sheetCopyId={null}
          cell={{
            tableInstanceId: 41,
            periodKey: 202602,
            rowKey: 'R1',
            rowLabel: 'Sea Water Intake',
            columnCode: 'Permit',
            isCalculated: false,
            isReadOnly: false,
            value: '11',
            ...cell,
          }}
        />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

function pickPermit(name: RegExp): void {
  fireEvent.click(screen.getByRole('textbox', { name: 'permits.history.permit' }));
  fireEvent.click(screen.getByRole('option', { name }));
}

describe('RowPermitSection (j1)', () => {
  it('j1b: the permit cell lists the history with who/when; Undo last undoes this row', async () => {
    undoRowPermit.mockResolvedValue(seaWaterRow());
    show({});

    const items = document.querySelectorAll('[data-permit-history-item]');
    expect(items).toHaveLength(2);
    expect(items[0]?.textContent).toContain('"until":"14.02.2026"');
    expect(items[1]?.textContent).toContain('"from":"15.02.2026"');
    expect(items[1]?.textContent).toContain('"user":"e2e-admin"');

    fireEvent.click(screen.getByRole('button', { name: 'permits.history.undo' }));
    await waitFor(() => expect(undoRowPermit).toHaveBeenCalledWith(3, { tableInstanceId: 41, rowKey: 'R1', columnCode: 'Permit' }));
  });

  it('j1a: Add permit from date — the default date is the day after the current permit ends', async () => {
    addRowPermit.mockResolvedValue(seaWaterRow());
    show({}, []);

    expect(screen.queryByRole('button', { name: 'permits.history.undo' })).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'permits.history.add' }));
    pickPermit(/KZ46VCZ14622410/);
    expect(screen.queryByRole('option', { name: /KZ29VCZ14622425/ })).toBeNull();
    expect(document.querySelector('[data-add-keeps]')?.textContent).toContain('"until":"14.02.2026","from":"15.02.2026"');
    fireEvent.click(screen.getByRole('button', { name: 'permits.history.addSubmit' }));

    await waitFor(() =>
      expect(addRowPermit).toHaveBeenCalledWith(3, {
        tableInstanceId: 41,
        rowKey: 'R1',
        columnCode: 'Permit',
        registryEntryId: 12,
        validFrom: '2026-02-15',
      }),
    );
    await waitFor(() => expect(document.querySelector('[data-permit-added]')).not.toBeNull());
  });

  it('j4: a gap names the days; «move the start date» re-submits the suggested date', async () => {
    addRowPermit.mockRejectedValueOnce(
      new EcrApiError({
        title: 'Gap',
        status: 422,
        errorCode: 'ECR-CELL-4223',
        correlationId: 'c',
        extensions2: { messageKey: 'err.ECR-CELL-4223.permitGap', next: 'KZ46VCZ14622410', days: '10.02–14.02.2026', suggestedFrom: '10.02.2026' },
      }),
    );
    addRowPermit.mockResolvedValueOnce(seaWaterRow());
    show({}, []);
    fireEvent.click(screen.getByRole('button', { name: 'permits.history.add' }));
    pickPermit(/KZ46VCZ14622410/);
    fireEvent.click(screen.getByRole('button', { name: 'permits.history.addSubmit' }));

    const alert = await screen.findByRole('alert');
    const move = within(alert).getByRole('button', { name: /permits\.history\.gapMove .*"date":"10\.02\.2026"/ });
    expect(within(alert).getByRole('button', { name: /permits\.history\.gapAdd/ })).toBeTruthy();

    // Fresh eyes 09.11 (j4 at 1335): the fixes are text links (wrap like text, no button nowrap) stacked in a column —
    // nothing runs past the narrow Inspector.
    expect(move.className).toMatch(/Anchor/);
    expect(move.className).not.toMatch(/Button/);
    expect(alert.querySelector('[data-gap-actions]')?.className).toMatch(/Stack/);
    fireEvent.click(move);

    await waitFor(() => expect(addRowPermit).toHaveBeenLastCalledWith(3, expect.objectContaining({ validFrom: '2026-02-10' })));
  });

  it('j2: the split month cell shows the parts; Edit parts opens the split dialog for it', () => {
    show({ columnCode: 'Feb', value: '12.4' });

    expect(document.querySelectorAll('[data-permit-part]')).toHaveLength(2);
    fireEvent.click(screen.getByRole('button', { name: 'permits.history.editParts' }));
    expect(cellSplitRequest()).toEqual({
      documentId: 3,
      periodKey: 202602,
      tableInstanceId: 41,
      tableDefId: 701,
      sheetCopyId: null,
      rowKey: 'R1',
      columnCode: 'Feb',
    });
  });

  it('a calculated cell of a row with history says it is not split', () => {
    show({ columnCode: 'Total', isCalculated: true, value: '3' });

    expect(document.querySelector('[data-permit-calculated-note]')?.textContent).toBe('permits.history.calculatedNotSplit');
  });
});
