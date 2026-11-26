import { describe, expect, it, vi } from 'vitest';
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

/** Мінімальний `h` (сніпдом) для `columnTemplate`: вузол із класом, атрибутами й дітьми. */
interface Node {
  readonly tag: string;
  readonly props: Record<string, unknown>;
  readonly children: unknown;
}
const h = (tag: string, props: Record<string, unknown>, children: unknown): Node => ({ tag, props, children });

function walk(node: unknown, found: Node[] = []): Node[] {
  if (Array.isArray(node)) node.forEach((child) => walk(child, found));
  else if (node !== null && typeof node === 'object' && 'tag' in node) {
    found.push(node as Node);
    walk((node as Node).children, found);
  }
  return found;
}

function headerOf(columns: ReturnType<typeof gridColumns>, code: string): Node[] {
  const found = columns.find((c) => c.prop === code);
  const template = found?.columnTemplate as ((hh: typeof h, column: unknown) => unknown) | undefined;
  if (template === undefined) throw new Error('немає columnTemplate');
  return walk(template(h, found));
}

function cellProps(columns: ReturnType<typeof gridColumns>, code: string, rowKey: string): Record<string, unknown> {
  const found = columns.find((c) => c.prop === code);
  return (found?.cellProperties as (p: never) => Record<string, unknown>)({ model: { __rowKey: rowKey }, prop: code } as never);
}

describe('DOCUX L2: заголовок і комірка «потрібне значення»', () => {
  it('BUG-13: колонка, обов’язкова лише для частини рядків (методологія), — своя підказка, не «This column is required.»', () => {
    const columns = gridColumns(slice({ columns: [column({ isRequiredByMethodology: true })] }), false, NoLocalFlags, {}, noRequiredInput);
    const props = (columns.find((c) => c.prop === 'C1')?.columnProperties as (p: never) => Record<string, unknown>)(undefined as never);

    expect(String(props['title'])).toContain('columnRequiredSomeRows');
    expect(props['aria-required']).toBe('true');
  });

  it('зірочка — окремий знак `ecr-header-required` (aria-hidden); з порожніми комірками — кнопка з кількістю, що веде до першої', () => {
    const goTo = vi.fn();
    const columns = gridColumns(
      slice({ columns: [column({ isRequired: true })] }),
      false,
      NoLocalFlags,
      {},
      noRequiredInput,
      undefined, undefined, undefined, undefined, undefined, undefined, undefined, undefined, undefined, undefined, undefined, undefined, undefined,
      { counts: new Map([['C1', 3]]), goTo },
    );
    const nodes = headerOf(columns, 'C1');
    const star = nodes.find((node) => node.props['class'] === 'ecr-header-required');
    expect(star?.children).toBe(' *');
    expect(star?.props['aria-hidden']).toBe('true');

    const count = nodes.find((node) => node.props['class'] === 'ecr-header-required-count');
    expect(count?.children).toBe('3');
    expect(String(count?.props['aria-label'])).toContain('grid.needsInput.columnCount');
    (count?.props['onClick'] as (event: Event) => void)(new Event('click'));
    expect(goTo).toHaveBeenCalledWith('C1');
  });

  it('L2-1: порожня обов’язкова комірка — клас `ecr-cell-needs-input--block`, aria-required/aria-invalid, пояснення в title; старого «!» немає', () => {
    const needs = { blocked: new Map([['R1:C1', 'Required — hint']]), warning: new Map<string, string>() };
    const columns = gridColumns(slice({ columns: [column({ isRequired: true })] }), false, NoLocalFlags, {}, needs);
    const props = cellProps(columns, 'C1', 'R1');

    expect(String(props['class'])).toContain('ecr-cell-needs-input ecr-cell-needs-input--block');
    expect(String(props['class'])).not.toContain('required-input');
    expect(props['data-needs-input']).toBe('block');
    expect(props['aria-required']).toBe('true');
    expect(props['aria-invalid']).toBe('true');
    expect(String(props['title'])).toContain('Required — hint');

    const warn = gridColumns(slice(), false, NoLocalFlags, {}, { blocked: new Map(), warning: new Map([['R1:C1', 'w']]) });
    expect(String(cellProps(warn, 'C1', 'R1')['class'])).toContain('ecr-cell-needs-input--warn');
  });
});

