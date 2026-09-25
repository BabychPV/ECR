import { describe, expect, it } from 'vitest';
import type { ColumnDto, TableSliceDto } from '@/api/types';
import { gridColumns } from '../DocumentGrid';
import { NoLocalFlags } from '../cellState';

/**
 * Візуальний індикатор обов'язковості колонки НА СІТЦІ, ДО спроби зберегти
 * (директива «зірочка в заголовку»). До цього `ColumnDef.IsRequired` долітав
 * до фронтенду (`ColumnDto.isRequired`) і ніде не використовувався в рендері
 * — оператор дізнавався про обов'язкову колонку лише ПІСЛЯ відхиленого
 * `PATCH /cells`.
 *
 * ⚠ Дві незалежні осі — звичайна структурна обов'язковість (`isRequired`) і
 * обов'язковий вхід чинної методології (`isRequiredByMethodology`,
 * `GetTableSliceHandler.RequiredByMethodologyColumnIdsAsync`) — позначаються
 * ОДНАКОВО: з погляду оператора джерело вимоги значення не має
 * (`02-contracts.md`, `ColumnDto.IsRequiredByMethodology`).
 *
 * ⚠ `gridColumns` — чиста функція побудови опису колонок для RevoGrid, тому
 * доступна для одиничного тесту без монтування веб-компонента (той самий
 * підхід, що й `gridColumns.saveError.test.ts`).
 */

function column(overrides: Partial<ColumnDto> = {}): ColumnDto {
  return {
    id: 1,
    code: 'C1',
    header: 'Колонка 1',
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

function nameOf(columns: ReturnType<typeof gridColumns>, columnCode: string): string {
  const found = columns.find((c) => c.prop === columnCode);
  if (found === undefined) {
    throw new Error(`Немає колонки ${columnCode}`);
  }

  return String(found.name);
}

describe('gridColumns — індикатор обов\'язковості колонки в заголовку', () => {
  it('звичайна колонка — заголовок без зірочки', () => {
    const columns = gridColumns(slice(), false, NoLocalFlags, {}, noRequiredInput);

    expect(nameOf(columns, 'C1')).toBe('Колонка 1');
  });

  it('ColumnDef.IsRequired — зірочка в заголовку', () => {
    const withRequired = slice({ columns: [column({ isRequired: true })] });
    const columns = gridColumns(withRequired, false, NoLocalFlags, {}, noRequiredInput);

    expect(nameOf(columns, 'C1')).toBe('Колонка 1 *');
  });

  it('обов\'язковий вхід чинної методології (IsRequiredByMethodology) — та сама зірочка', () => {
    const withMethodology = slice({ columns: [column({ isRequiredByMethodology: true })] });
    const columns = gridColumns(withMethodology, false, NoLocalFlags, {}, noRequiredInput);

    expect(nameOf(columns, 'C1')).toBe('Колонка 1 *');
  });

  it('обидві вісі одночасно — зірочка рівно одна, не дублюється', () => {
    const both = slice({ columns: [column({ isRequired: true, isRequiredByMethodology: true })] });
    const columns = gridColumns(both, false, NoLocalFlags, {}, noRequiredInput);

    expect(nameOf(columns, 'C1')).toBe('Колонка 1 *');
  });

  it('зірочка додається ПІСЛЯ одиниці виміру, а не замінює її', () => {
    const withUnit = slice({ columns: [column({ isRequired: true, unitSymbol: 'кг' })] });
    const columns = gridColumns(withUnit, false, NoLocalFlags, {}, noRequiredInput);

    expect(nameOf(columns, 'C1')).toBe('Колонка 1, кг *');
  });

  it('заголовок обов\'язкової колонки несе текстове пояснення (columnProperties.title) для читалки екрана', () => {
    const withRequired = slice({ columns: [column({ isRequired: true })] });
    const columns = gridColumns(withRequired, false, NoLocalFlags, {}, noRequiredInput);
    const found = columns.find((c) => c.prop === 'C1');

    expect(found?.columnProperties).toBeTypeOf('function');
    const props = (found?.columnProperties as (p: never) => Record<string, unknown>)(undefined as never);

    // ⚠ Каталог не завантажений у цьому тесті (той самий випадок, що й
    // `decide()` у `permissions.test.ts`): `t()` повертає позначений
    // ключ, а не переклад. Тест доводить, що ПОЯСНЕННЯ взагалі є (а не
    // порожній рядок чи `undefined`) — саме той ключ каталогу
    // (`grid.columnRequiredHint`, `09-seed.sql`) і несе людський текст.
    expect(String(props['title'])).toContain('columnRequiredHint');
  });

  it('колонка без вимоги підказки в заголовку не отримує', () => {
    /*
     * ✒ `U-05`: твердження звужено з «`columnProperties` немає взагалі» до
     * «підказки немає»: числова колонка тепер законно має
     * `columnProperties` з класом вирівнювання. Суть перевірки та сама:
     * зірочка й її пояснення не з’являються там, де вимоги немає.
     */
    const columns = gridColumns(slice(), false, NoLocalFlags, {}, noRequiredInput);
    const found = columns.find((c) => c.prop === 'C1');

    const props =
      typeof found?.columnProperties === 'function'
        ? ((found.columnProperties({} as never) ?? {}) as Record<string, unknown>)
        : {};

    expect(props['title']).toBeUndefined();
    expect(String(found?.name ?? '')).not.toContain('*');
  });
});
