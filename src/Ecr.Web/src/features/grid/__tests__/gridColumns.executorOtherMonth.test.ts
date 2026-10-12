import { describe, expect, it } from 'vitest';
import type { ColumnDto, TableSliceDto } from '@/api/types';
import { hasMonthAssignment } from '@/features/editAssignments/model';
import type { MyEditAssignment } from '@/features/editAssignments/types';
import { gridColumns } from '../DocumentGrid';
import { NoLocalFlags } from '../cellState';

/** D-3: виконавець з передачею ІНШОГО місяця бачить «цей місяць вам не передано», а не «рівень гранта замалий». */

function column(overrides: Partial<ColumnDto> = {}): ColumnDto {
  return {
    id: 1,
    code: 'C1',
    header: 'C1',
    dataType: 'Decimal',
    ordinal: 1,
    isReadOnly: false,
    isRequired: false,
    isRequiredByMethodology: false,
    displayFormat: null,
    defaultValue: null,
    lookupRegistryDefId: null,
    unitId: null,
    unitSymbol: null,
    ...overrides,
  };
}

function slice(overrides: Partial<TableSliceDto> = {}): TableSliceDto {
  return {
    tableInstanceId: 1,
    periodKey: 202609,
    columns: [column()],
    rows: [
      { rowKey: 'R1', ordinal: 1, rowKind: 'Item', label: null, rowVersion: '0x01', cells: {}, isOrphaned: false },
    ],
    cellPermissions: {},
    cellConfirmations: {},
    ...overrides,
  };
}

const noRequiredInput = { blocked: new Map<string, string>(), warning: new Map<string, string>() };

function titleOf(columns: ReturnType<typeof gridColumns>, rowKey: string): string | undefined {
  const found = columns.find((c) => c.prop === 'C1');
  if (found === undefined || typeof found.cellProperties !== 'function') throw new Error('cellProperties відсутній');

  const props = found.cellProperties({ model: { __rowKey: rowKey } } as never) ?? {};
  return props.title as string | undefined;
}

function mine(overrides: Partial<MyEditAssignment> = {}): MyEditAssignment {
  return {
    assignmentId: 1,
    documentId: 10,
    documentKey: 'D',
    delegatedByDisplayName: 'Owner',
    filledCount: 0,
    note: null,
    recipientCount: 1,
    returnReason: null,
    slice: { monthNumber: 1, periodKey: null, sheetCode: null, tableCode: null },
    status: 'Assigned',
    statusChangedAt: null,
    version: 'v',
    ...overrides,
  };
}

const denied = slice({ cellPermissions: { 'R1:C1': 'InsufficientGrantLevel' } });

describe('gridColumns - підказка непереданого місяця (D-3)', () => {
  it('без передачі місяця - загальна причина сервера', () => {
    expect(titleOf(gridColumns(denied, false, NoLocalFlags, {}, noRequiredInput), 'R1')).toContain('deny.InsufficientGrantLevel');
  });

  it('з передачею місяця - «цей місяць не передано»', () => {
    const columns = gridColumns(denied, false, NoLocalFlags, {}, noRequiredInput, new Map(), new Map(), new Map(), new Set(), null, null, new Set(), new Map(), true);

    expect(titleOf(columns, 'R1')).toContain('deny.AssignmentOtherMonth');
    expect(titleOf(columns, 'R1')).not.toContain('deny.InsufficientGrantLevel');
  });

  it('інша причина (напр. NoGrant) не підміняється', () => {
    const noGrant = slice({ cellPermissions: { 'R1:C1': 'NoGrant' } });
    const columns = gridColumns(noGrant, false, NoLocalFlags, {}, noRequiredInput, new Map(), new Map(), new Map(), new Set(), null, null, new Set(), new Map(), true);

    expect(titleOf(columns, 'R1')).toContain('deny.NoGrant');
  });
});

describe('hasMonthAssignment', () => {
  const view = (items: MyEditAssignment[]) => ({ assignedToMe: items, awaitingMyValidation: [] });

  it('так - місячна передача цього документа', () => {
    expect(hasMonthAssignment(view([mine()]), 10)).toBe(true);
  });

  it('ні - інший документ, не місячний зріз або порожньо', () => {
    expect(hasMonthAssignment(view([mine({ documentId: 11 })]), 10)).toBe(false);
    expect(hasMonthAssignment(view([mine({ slice: { monthNumber: null, periodKey: null, sheetCode: 'AIR', tableCode: null } })]), 10)).toBe(false);
    expect(hasMonthAssignment(undefined, 10)).toBe(false);
  });
});
