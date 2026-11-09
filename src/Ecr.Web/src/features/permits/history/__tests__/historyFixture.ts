import type { DocumentPermitHistory, RowPermitHistory } from '../api';

/** Sea Water Intake: KZ29…425 until 14.02.2026, KZ46…410 from 15.02.2026; February 12.4 not split yet (mockup j1b). */
export function seaWaterRow(overrides: Partial<RowPermitHistory> = {}): RowPermitHistory {
  return {
    tableDefId: 701,
    tableCode: 'WATER',
    sheetCopyId: null,
    rowKey: 'R1',
    columnCode: 'Permit',
    permitSource: 'row',
    permits: [
      { segmentId: null, registryEntryId: 11, permit: 'KZ29VCZ14622425', from: null, until: '2026-02-14', addedBy: null, addedAt: null },
      {
        segmentId: 5,
        registryEntryId: 12,
        permit: 'KZ46VCZ14622410',
        from: '2026-02-15',
        until: null,
        addedBy: 'e2e-admin',
        addedAt: '2026-11-09T08:12:00Z',
      },
    ],
    splitMonths: [
      {
        month: '2026-02-01',
        columnCodes: ['Feb'],
        parts: [
          { partNo: 1, registryEntryId: 11, permit: 'KZ29VCZ14622425', dayFrom: '2026-02-01', dayTo: '2026-02-14' },
          { partNo: 2, registryEntryId: 12, permit: 'KZ46VCZ14622410', dayFrom: '2026-02-15', dayTo: '2026-02-28' },
        ],
        cells: [{ columnCode: 'Feb', total: '12.4', parts: [null, null], isSplit: false }],
      },
    ],
    ...overrides,
  };
}

export function historyOf(...rows: RowPermitHistory[]): DocumentPermitHistory {
  return { header: [], rows, cellsNeedingSplit: 1 };
}
