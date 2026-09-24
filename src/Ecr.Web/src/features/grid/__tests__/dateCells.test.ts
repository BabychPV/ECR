import { describe, expect, it } from 'vitest';
import type { ColumnDto, TableSliceDto } from '@/api/types';
import { formatDate } from '@/shared/format';
import { captureEdit } from '../edits';
import { cellDisplay, editorValueOf } from '../cellValue';
import { gridColumns } from '../DocumentGrid';
import { NoLocalFlags } from '../cellState';

/**
 * Дата в сітці після перезавантаження.
 *
 * ⛔ Комірка `Date` показувала сирий запис сховища `2026-09-15T00:00:00`.
 * Тепер — `formatDate` (`shared/format`), як і решта продукту; редактор
 * отримує саму дату, а вихід із нього без змін не стає правкою.
 */

function column(overrides: Partial<ColumnDto>): ColumnDto {
  return {
    id: 1,
    code: 'C1',
    header: 'C1',
    dataType: 'Bool',
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

function slice(col: ColumnDto, value: unknown): TableSliceDto {
  return {
    tableInstanceId: 1,
    periodKey: 202609,
    columns: [col],
    rows: [
      { rowKey: 'R1', ordinal: 1, rowKind: 'Static', label: null, rowVersion: 'v1', cells: { C1: value }, isOrphaned: false },
    ],
    cellPermissions: {},
    cellConfirmations: {},
  };
}

const noRequiredInput = { blocked: new Map<string, string>(), warning: new Map<string, string>() };

describe('Дата в сітці — без години опівночі сховища', () => {
  const date = column({ dataType: 'Date' });

  it('показ — форматом продукту (`formatDate`), не сирим записом', () => {
    const shown = cellDisplay('2026-09-15T00:00:00', date);

    expect(shown).not.toContain('T00');
    expect(shown).toBe(formatDate('2026-09-15'));
  });

  it('редактор отримує саму дату', () => {
    expect(editorValueOf('2026-09-15T00:00:00', date)).toBe('2026-09-15');
  });

  it('вихід із редактора без змін — не правка', () => {
    expect(captureEdit(slice(date, '2026-09-15T00:00:00'), { columnCode: 'C1', rowKey: 'R1', raw: '2026-09-15' })).toBeNull();
    expect(captureEdit(slice(date, '2026-09-15T00:00:00'), { columnCode: 'C1', rowKey: 'R1', raw: '2026-09-16' })).not.toBeNull();
  });

  it('колонка Date має шаблон показу', () => {
    const [grid] = gridColumns(slice(date, '2026-09-15T00:00:00'), false, NoLocalFlags, {}, noRequiredInput);
    const template = grid?.cellTemplate as ((h: unknown, props: { value?: unknown }) => string) | undefined;

    expect(template?.(null, { value: '2026-09-15T00:00:00' })).toBe(formatDate('2026-09-15'));
  });

  it('текст, що не є датою, показується як є', () => {
    expect(cellDisplay('15.09.2026', date)).toBe('15.09.2026');
  });
});
