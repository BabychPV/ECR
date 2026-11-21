import { describe, expect, it } from 'vitest';
import type { ColumnDto, TableSliceDto } from '@/api/types';
import { cellKey } from '../permissions';
import { createGridRowsCache, gridRows } from '../gridRowModels';

/**
 * PSS-18: модель рядків сітки — з кешем НА РЯДОК. Правка однієї комірки (нове перекриття) перебудовує лише свій
 * рядок; решта 499 моделей — ті самі об'єкти, і значення — рівно ті, що дав би повний перебудов (`gridRows`).
 *
 * Мутаційні докази (не в коміті):
 *  - кеш вимкнено (`hit !== undefined && false`) → червоний «одна правка — один новий рядок»;
 *  - без звірки перекриттів рядка (`sameOverrides(hit.overrides, own)` прибрано) → червоні «одна правка» і «повернення
 *    до збереженого після введення в модель» (повторюється модель, у яку RevoGrid уже вписав введене).
 */

const Rows = 500;
const Columns = 60;

function column(code: string, ordinal: number): ColumnDto {
  return {
    code,
    dataType: ordinal % 2 === 0 ? 'Decimal' : 'String',
    defaultValue: ordinal === 3 ? '7' : null,
    displayFormat: null,
    header: code,
    id: ordinal + 1,
    isReadOnly: false,
    isRequired: false,
    isRequiredByMethodology: false,
    lookupRegistryDefId: null,
    ordinal,
    unitId: null,
    unitSymbol: null,
  } as ColumnDto;
}

function slice(): TableSliceDto {
  const columns = Array.from({ length: Columns }, (_, c) => column(`C${String(c)}`, c));

  return {
    cellConfirmations: {},
    cellPermissions: {},
    periodKey: 202609,
    tableInstanceId: 1,
    columns,
    rows: Array.from({ length: Rows }, (_, r) => ({
      cells: Object.fromEntries(columns.filter((_, c) => c !== 3).map((col, c) => [col.code, c % 2 === 0 ? `${String(r)}.5000` : `t${String(r)}`])),
      isOrphaned: false,
      label: r % 10 === 0 ? null : `Row ${String(r)}`,
      ordinal: r,
      rowKey: `r:${String(r)}`,
      rowKind: 'Item',
      rowVersion: 'v1',
    })),
  } as TableSliceDto;
}

describe('PSS-18: кеш моделі рядків сітки', () => {
  it('500×60, одна правка — новий об\'єкт лише в її рядку, решта 499 ті самі; значення = повний перебудов', () => {
    const data = slice();
    const build = createGridRowsCache();

    const first = build(data, new Map());
    expect(first).toEqual(gridRows(data, new Map()));

    const overrides = new Map<string, unknown>([[cellKey('r:250', 'C10'), '42']]);
    const second = build(data, overrides);

    const changed = second.filter((row, index) => row !== first[index]).map((row) => row.__rowKey);
    expect(changed).toEqual(['r:250']);
    expect(second[250]?.['C10']).toBe('42');
    expect(second).toEqual(gridRows(data, overrides));

    // Друге натискання в тому самому рядку — знову лише він.
    const third = build(data, new Map([[cellKey('r:250', 'C10'), '421']]));
    expect(third.filter((row, index) => row !== second[index]).map((row) => row.__rowKey)).toEqual(['r:250']);
  });

  it('новий рядок у зрізі (відповідь на PATCH) — перебудовано лише його; колонки змінились — усі', () => {
    const data = slice();
    const build = createGridRowsCache();
    const first = build(data);

    const patched = { ...data, rows: data.rows.map((row, i) => (i === 7 ? { ...row, cells: { ...row.cells, C0: '99' } } : row)) };
    const second = build(patched);
    expect(second.filter((row, index) => row !== first[index]).map((row) => row.__rowKey)).toEqual(['r:7']);
    expect(second[7]?.['C0']).toBe('99');

    const reshaped = { ...patched, columns: [...patched.columns] };
    const third = build(reshaped);
    expect(third.every((row, index) => row !== second[index])).toBe(true);
  });

  it('повернення до збереженого після введення: модель, у яку RevoGrid вписав введене, НЕ повторюється', () => {
    const data = slice();
    const build = createGridRowsCache();
    const first = build(data, new Map());
    const original = first[3]?.['C1'];

    // RevoGrid пише введене прямо в модель (`source[i][prop] = val`), сітка кладе правку в сховище.
    (first[3] as Record<string, unknown>)['C1'] = 'typed';
    build(data, new Map([[cellKey('r:3', 'C1'), 'typed']]));

    // Undo / «Discard»: правки рядка вже немає — ті самі перекриття, що й на початку.
    const reverted = build(data, new Map());
    expect(reverted[3]).not.toBe(first[3]);
    expect(reverted[3]?.['C1']).toBe(original);
    expect(reverted[4]).toBe(first[4]);
  });

  it('RevoGrid вписав у модель значення, яке не потрапило в перекриття (інший текст / заблокована комірка) — рядок будується заново', () => {
    const data = slice();
    const build = createGridRowsCache();
    const first = build(data, new Map());
    const stored = first[1]?.['C0'];

    // Те саме число іншим текстом: `captureEdit` дав null, перекриття не з'явилось, а модель уже змінена.
    (first[1] as Record<string, unknown>)['C0'] = '1,5';
    // Правка в ІНШОМУ рядку — кеш рядка 1 має влучити, але не віддати незбережене.
    const second = build(data, new Map([[cellKey('r:2', 'C1'), 'x']]));
    expect(second[1]).not.toBe(first[1]);
    expect(second[1]?.['C0']).toBe(stored);
    expect(second).toEqual(gridRows(data, new Map([[cellKey('r:2', 'C1'), 'x']])));

    // Незайманий рядок лишається тим самим об'єктом (виграш PSS-18).
    expect(second[4]).toBe(first[4]);
    // Після перебудови наступний виклик знову влучає в кеш.
    expect(build(data, new Map([[cellKey('r:2', 'C1'), 'x']]))[1]).toBe(second[1]);
  });
});
