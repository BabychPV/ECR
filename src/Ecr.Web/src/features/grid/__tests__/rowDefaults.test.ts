import { describe, expect, it } from 'vitest';
import type { ColumnDto, RowDto, TableSliceDto } from '@/api/types';
import { captureEdit } from '@/features/grid/edits';
import { columnTotals } from '@/features/grid/gridTotals';
import { effectiveDefault } from '@/features/grid/rowDefaults';

/**
 * ✎ B175: дефолти комірок РЯДКА шаблону (`RowDto.defaults`) на клієнті — у тих самих трьох місцях, що дефолт
 * колонки: показ (`gridRows` через `effectiveDefault`), підсумок (`gridTotals`) і «клік повз редактор не пише
 * показаний default» (`edits.captureEdit`). Пріоритет — як на сервері: записане > рядок > колонка; `''` рядка
 * прибирає дефолт колонки.
 *
 * Мутація: `effectiveDefault` повертає лише `column.defaultValue` (клієнт ігнорує `row.defaults`) — червоніють усі
 * три блоки.
 */
function column(overrides: Partial<ColumnDto> = {}): ColumnDto {
  return {
    id: 1,
    code: 'C1',
    header: 'Обсяг',
    dataType: 'Decimal',
    ordinal: 1,
    isReadOnly: false,
    isRequired: false,
    isRequiredByMethodology: false,
    displayFormat: null,
    defaultValue: '5',
    lookupRegistryDefId: null,
    unitId: null,
    unitSymbol: null,
    ...overrides,
  };
}

function row(rowKey: string, cells: Record<string, unknown>, defaults?: Record<string, string>): RowDto {
  return {
    rowKey,
    ordinal: 1,
    rowKind: 'Item',
    label: null,
    rowVersion: '0x01',
    cells,
    isOrphaned: false,
    ...(defaults === undefined ? {} : { defaults }),
  } as RowDto;
}

function slice(rows: RowDto[], columns: ColumnDto[] = [column()]): TableSliceDto {
  return { tableInstanceId: 1, periodKey: 202601, columns, rows, cellPermissions: {}, cellConfirmations: {} };
}

describe('B175: дефолт рядка', () => {
  it('effectiveDefault: рядок перекриває колонку, порожній рядок прибирає її, без дефолтів рядка — колонка', () => {
    const c = column();

    expect(effectiveDefault(row('r1', {}, { C1: '7' }), c)).toBe('7');
    expect(effectiveDefault(row('r2', {}, { C1: '' }), c)).toBe('');
    expect(effectiveDefault(row('r3', {}), c)).toBe('5');
    expect(effectiveDefault(row('r4', {}, { OTHER: '9' }), c)).toBe('5');
  });

  it('підсумок рахує видимий дефолт рядка, а прибраний — ні', () => {
    const totals = columnTotals(
      slice([row('r1', {}, { C1: '7' }), row('r2', {}, { C1: '' }), row('r3', {}), row('r4', { C1: '1' }, { C1: '7' })]),
    );

    // r1 = 7 (рядок), r2 — порожньо (прибрано), r3 = 5 (колонка), r4 = 1 (записане перемагає).
    expect(totals.get('C1')).toEqual({ sum: '13', count: 3 });
  });

  it('клік повз редактор із показаним дефолтом РЯДКА не пише, а дефолт колонки там уже не показаний', () => {
    const data = slice([row('R1', {}, { C1: '7' })]);

    expect(captureEdit(data, { columnCode: 'C1', rowKey: 'R1', raw: '7', untouched: true })).toBeNull();

    // `5` — дефолт колонки, якого в цьому рядку не видно: це вже введення, не відлуння.
    expect(captureEdit(data, { columnCode: 'C1', rowKey: 'R1', raw: '5', untouched: true })?.pending.value).toBe('5');
  });
});
