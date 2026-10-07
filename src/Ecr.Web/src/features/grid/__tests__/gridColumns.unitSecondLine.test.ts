import { describe, expect, it } from 'vitest';
import type { ColumnDto, TableSliceDto } from '@/api/types';
import { gridColumns } from '../DocumentGrid';
import { NoLocalFlags } from '../cellState';

/**
 * UI-24: одиниця вимірювання — другим рядком у заголовку колонки (макет
 * `docs/design/hybrid`, «Fuel gas / 10³ m³»), а повний текст «Назва, одиниця»
 * лишається в `name` і в тексті для читалки.
 */

function column(overrides: Partial<ColumnDto> = {}): ColumnDto {
  return {
    id: 1,
    code: 'C1',
    header: 'Fuel gas',
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

function slice(columns: ColumnDto[]): TableSliceDto {
  return {
    tableInstanceId: 1,
    periodKey: 202609,
    columns,
    rows: [{ rowKey: 'R1', ordinal: 1, rowKind: 'Item', label: null, rowVersion: '0x01', cells: {}, isOrphaned: false }],
    cellPermissions: {},
    cellConfirmations: {},
  };
}

const noRequiredInput = { blocked: new Map<string, string>(), warning: new Map<string, string>() };

/** Розмітка, яку шаблон збудував би, — без Stencil: `h` збирає дерево. */
interface Node {
  readonly tag: string;
  readonly cls: string;
  readonly children: readonly (Node | string)[];
}

function h(tag: string, props: { class?: string } | null, children?: unknown): Node {
  const list = Array.isArray(children) ? children : children === undefined ? [] : [children];

  return { tag, cls: props?.class ?? '', children: list as (Node | string)[] };
}

function textOf(node: Node | string | undefined): string {
  if (node === undefined) return '';
  return typeof node === 'string' ? node : node.children.map(textOf).join('');
}

function header(columns: ReturnType<typeof gridColumns>): Node | null {
  const found = columns.find((c) => c.prop === 'C1');
  if (found?.columnTemplate === undefined) return null;

  return (found.columnTemplate as unknown as (create: typeof h) => Node)(h);
}

describe('gridColumns — одиниця другим рядком у заголовку (UI-24)', () => {
  it('колонка з одиницею: назва й одиниця — окремі рядки, текст для читалки «Назва, одиниця»', () => {
    const node = header(gridColumns(slice([column({ unitSymbol: '10³ m³' })]), false, NoLocalFlags, {}, noRequiredInput));

    expect(node).not.toBeNull();
    const parts = (node as Node).children as Node[];

    expect(parts.map((part) => part.cls)).toEqual(['ecr-header-name', 'ecr-header-sr', 'ecr-header-unit']);
    expect(textOf(parts[0])).toBe('Fuel gas');
    expect(textOf(parts[2])).toBe('10³ m³');
    expect(textOf(node as Node)).toBe('Fuel gas, 10³ m³');
  });

  it('зірочка обов\'язковості лишається біля назви', () => {
    const node = header(
      gridColumns(slice([column({ unitSymbol: 'kg', isRequired: true })]), false, NoLocalFlags, {}, noRequiredInput),
    );

    expect(textOf(((node as Node).children as Node[])[0])).toBe('Fuel gas *');
  });

  it('без одиниці шаблону немає — заголовок той самий, що й був, висота не змінюється', () => {
    const columns = gridColumns(slice([column()]), false, NoLocalFlags, {}, noRequiredInput);

    expect(header(columns)).toBeNull();
    expect(String(columns.find((c) => c.prop === 'C1')?.name)).toBe('Fuel gas');
  });
});
