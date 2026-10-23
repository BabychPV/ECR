import { describe, expect, it } from 'vitest';
import type { ColumnDto, TableSliceDto } from '@/api/types';
import { gridColumns } from '../DocumentGrid';
import { NoLocalFlags } from '../cellState';

/**
 * D-441 (AN-128): колонка місяця k таблиці `MonthsInColumns` позначається станом аркуша за період k —
 * «Подано» / «Затверджено» у рядку назви заголовка (`monthStatus` з `GET …/submission-target`).
 *
 * ⛔ На старому коді параметра не було, і заголовок місячної колонки нічого не казав про її період.
 */

function column(overrides: Partial<ColumnDto> = {}): ColumnDto {
  return {
    id: 1,
    code: 'JAN',
    header: 'January',
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
    periodKey: 202602,
    columns,
    rows: [{ rowKey: 'R1', ordinal: 1, rowKind: 'Item', label: null, rowVersion: '0x01', cells: {}, isOrphaned: false }],
    cellPermissions: {},
    cellConfirmations: {},
  };
}

const noRequiredInput = { blocked: new Map<string, string>(), warning: new Map<string, string>() };

interface Node {
  readonly tag: string;
  readonly cls: string;
  readonly attrs: Record<string, unknown>;
  readonly children: readonly (Node | string)[];
}

function h(tag: string, props: Record<string, unknown> | null, children?: unknown): Node {
  const list = Array.isArray(children) ? children : children === undefined ? [] : [children];

  return { tag, cls: String(props?.['class'] ?? ''), attrs: props ?? {}, children: list as (Node | string)[] };
}

function textOf(node: Node | string | undefined): string {
  if (node === undefined) return '';
  return typeof node === 'string' ? node : node.children.map(textOf).join('');
}

function find(node: Node | string, cls: string): Node | null {
  if (typeof node === 'string') return null;
  if (node.cls.split(' ').includes(cls)) return node;
  for (const child of node.children) {
    const hit = find(child, cls);
    if (hit !== null) return hit;
  }

  return null;
}

function header(columns: ReturnType<typeof gridColumns>, code: string): Node | null {
  const found = columns.find((c) => c.prop === code);
  if (found?.columnTemplate === undefined) return null;

  return (found.columnTemplate as unknown as (create: typeof h) => Node)(h);
}

function columnsWith(monthStatus: ReadonlyMap<string, 'Submitted' | 'Approved'>, columns: ColumnDto[]): ReturnType<typeof gridColumns> {
  return gridColumns(
    slice(columns),
    false,
    NoLocalFlags,
    {},
    noRequiredInput,
    new Map(),
    new Map(),
    new Map(),
    new Set(),
    null,
    null,
    new Set(),
    new Map(),
    monthStatus,
  );
}

describe('gridColumns — стан періоду місячної колонки в заголовку (D-441)', () => {
  it('«Січень» поданого періоду: позначка Submitted у рядку назви; читалка чує «Назва, стан»', () => {
    const node = header(columnsWith(new Map([['JAN', 'Submitted']]), [column()]), 'JAN');

    expect(node).not.toBeNull();
    const mark = find(node as Node, 'ecr-header-month-status');
    expect(mark?.attrs['data-month-status']).toBe('Submitted');
    expect(mark?.cls).toContain('ecr-header-month-status--submitted');
    expect(textOf(mark ?? undefined)).toBe('⟦status.sheet.Submitted⟧');
    expect(textOf(node as Node)).toBe('January, ⟦status.sheet.Submitted⟧');
  });

  it('затверджений період — Approved; одиниця лишається другим рядком', () => {
    const node = header(columnsWith(new Map([['FEB', 'Approved']]), [column({ code: 'FEB', header: 'February', unitSymbol: 't' })]), 'FEB');

    expect(find(node as Node, 'ecr-header-month-status')?.attrs['data-month-status']).toBe('Approved');
    expect(textOf(find(node as Node, 'ecr-header-unit') ?? undefined)).toBe('t');
  });

  it('колонка без позначки (чернетка чи не місячна) — заголовок той самий, що й був', () => {
    const columns = columnsWith(new Map([['JAN', 'Submitted']]), [column({ code: 'TOTAL', header: 'Total' })]);

    expect(header(columns, 'TOTAL')).toBeNull();
    expect(String(columns.find((c) => c.prop === 'TOTAL')?.name)).toBe('Total');
  });
});
