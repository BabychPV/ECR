import { describe, expect, it } from 'vitest';
import { EcrApiError } from '@/api/client';
import type { ColumnDto, TableSliceDto } from '@/api/types';
import { formatMonthName } from '@/shared/format/period';
import { decide, dependentMonthsDetail } from '../permissions';
import { deniedCellReasons, dependentSheetsOf } from '../saveErrors';

/**
 * ✎ B79 (квіз 16.11, людина: «і користувач має побачити чому»): відмова `DependentMonthsLocked` називає АРКУШІ, чиї
 * затверджені місяці залежать від стовпця, — у підказці сірої комірки (зріз, `dependentMonths`) і в причині відхиленої
 * правки (відмова 403, `sheets`).
 *
 * ⛔ МУТАЦІЇ (локально): у `decide` не додавати `dependentMonthsDetail` — червоніє «підказка сірої комірки…»; у
 * `deniedCellReasons` не додавати деталь — червоніє «причина відхиленої правки…»; у `dependentMonthsDetail` не
 * відкидати аркуш без місяців — червоніє «аркуш без місяців…».
 */

function column(): ColumnDto {
  return {
    id: 7,
    code: 'FUEL',
    header: 'Fuel',
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
  };
}

function slice(dependentMonths?: TableSliceDto['dependentMonths']): TableSliceDto {
  return {
    tableInstanceId: 1,
    periodKey: 209910,
    columns: [column()],
    rows: [{ rowKey: 'R1', ordinal: 1, rowKind: 'Static', label: null, rowVersion: '0x01', cells: {}, isOrphaned: false }],
    cellPermissions: { 'R1:FUEL': 'DependentMonthsLocked' },
    cellConfirmations: {},
    ...(dependentMonths === undefined ? {} : { dependentMonths }),
  };
}

describe('B79: аркуші й місяці відмови DependentMonthsLocked', () => {
  it('підказка сірої комірки називає аркуш-читач і його затверджені місяці', () => {
    const decision = decide(slice({ FUEL: [{ sheetCode: 'SHB', months: [209903, 209901] }] }), 'R1', column());

    expect(decision.editable).toBe(false);
    expect(decision.reason).toBe('DependentMonthsLocked');
    expect(decision.hint).toContain(`SHB: ${formatMonthName(3)}, ${formatMonthName(1)}`);
  });

  it('без dependentMonths підказка — звичайний текст причини', () => {
    const plain = decide(slice(), 'R1', column());
    const withSheets = decide(slice({ FUEL: [{ sheetCode: 'SHB', months: [209903] }] }), 'R1', column());

    expect(withSheets.hint.startsWith(plain.hint)).toBe(true);
    expect(plain.hint).not.toContain('SHB');
  });

  it('аркуш без місяців у переліку не з’являється', () => {
    expect(
      dependentMonthsDetail([
        { sheetCode: 'SHB', months: [] },
        { sheetCode: 'SHC', months: [209904] },
      ]),
    ).toBe(`SHC: ${formatMonthName(4)}`);
  });

  it('причина відхиленої правки (403 sheets) називає аркуш і місяць', () => {
    const error = new EcrApiError({
      title: 'Forbidden',
      status: 403,
      errorCode: 'ECR-ACCS-0403',
      correlationId: 'c1',
      extensions2: {
        reason: 'DependentMonthsLocked',
        deniedCount: '1',
        months: [209903],
        sheets: [{ sheetCode: 'SHB', months: [209903] }, { bad: true }],
      },
    });

    expect(dependentSheetsOf(error)).toEqual([{ sheetCode: 'SHB', months: [209903] }]);

    const [reason] = deniedCellReasons(
      error,
      [{ rowKey: 'R1', columnCode: 'FUEL', value: '1', isEmpty: false, baseVersion: 'v1' }],
      undefined,
    );
    expect(reason?.message).toContain(`SHB: ${formatMonthName(3)}`);
  });
});
