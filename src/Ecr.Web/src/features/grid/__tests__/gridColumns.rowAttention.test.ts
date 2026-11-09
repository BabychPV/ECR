import { describe, expect, it } from 'vitest';
import type { ColumnDto, TableSliceDto } from '@/api/types';
import { NoLocalFlags } from '../cellState';
import { gridColumns } from '../DocumentGrid';
import { cellKey } from '../permissions';
import {
  coversPeriod,
  describeColumns,
  describeValidity,
  lockedColumnsOf,
  rowAttentionReason,
  rowLookupProblemsOf,
  tableAttentionOf,
} from '../rowAttention';

/**
 * ✎ UAT 08.11 (d2), Water Report, рядок «Sea Water Intake»: червоний рядок зі знаком «!» мав причину лише
 * кольором. Тепер підпис рядка несе причину СЛОВАМИ (втрачене посилання + закриті місяці діапазоном) і кнопку
 * «!», що відкриває Issues на цьому пункті.
 *
 * ⛔ Червоне без фіксу: шаблон підпису повертав лише `span.ecr-row-label` — без `ecr-row-attention-reason` і
 * без `data-row-attention-open`.
 */

const Months = ['JAN', 'FEB', 'MAR', 'APR', 'MAY', 'JUN', 'JUL', 'AUG', 'SEP'];
const Names = ['January', 'February', 'March', 'April', 'May', 'June', 'July', 'August', 'September'];

function column(code: string, header: string, ordinal: number): ColumnDto {
  return {
    id: ordinal + 1,
    code,
    header,
    dataType: 'Decimal',
    ordinal,
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

/** Рядок R1 «Sea Water Intake» з утраченим посиланням; січень–серпень закриті; R2 — звичайний. */
function slice(orphaned = true): TableSliceDto {
  const cellPermissions: Record<string, string> = {};
  for (const code of Months.slice(0, 8)) {
    cellPermissions[cellKey('R1', code)] = 'PeriodClosed';
    cellPermissions[cellKey('R2', code)] = 'PeriodClosed';
  }

  return {
    tableInstanceId: 597,
    periodKey: 202601,
    columns: Months.map((code, index) => column(code, Names[index] ?? code, index)),
    rows: [
      { rowKey: 'R1', ordinal: 1, rowKind: 'Item', label: 'Sea Water Intake', rowVersion: '0x01', cells: {}, isOrphaned: orphaned },
      { rowKey: 'R2', ordinal: 2, rowKind: 'Item', label: 'Fresh Water Intake', rowVersion: '0x01', cells: {}, isOrphaned: false },
    ],
    cellPermissions,
    cellConfirmations: {},
  } as TableSliceDto;
}

/** Колонка «Permit number» (Lookup на ціль Permits, id 50); у R1 — запис 7. */
function withPermitColumn(dto: TableSliceDto): TableSliceDto {
  return {
    ...dto,
    columns: [{ ...column('PermitNumber', 'Permit number', 99), dataType: 'Lookup', lookupRegistryDefId: 50 }, ...dto.columns],
    rows: dto.rows.map((row) => (row.rowKey === 'R1' ? { ...row, cells: { ...row.cells, PermitNumber: 7 } } : row)),
  };
}

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

function find(node: Node | string, cls: string): Node | null {
  if (typeof node === 'string') return null;
  if (node.cls.split(' ').includes(cls)) return node;
  for (const child of node.children) {
    const hit = find(child, cls);
    if (hit !== null) return hit;
  }

  return null;
}

function textOf(node: Node | string | null): string {
  if (node === null) return '';
  return typeof node === 'string' ? node : node.children.map(textOf).join('');
}

function labelCell(dto: TableSliceDto, rowKey: string, label: string): Node {
  const columns = gridColumns(dto, false, NoLocalFlags, {}, { blocked: new Map(), warning: new Map() });
  const labelColumn = columns[0];
  expect(labelColumn?.prop).toBe('__rowLabel');
  const template = labelColumn?.cellTemplate as unknown as (create: typeof h, cell: { model: Record<string, unknown> }) => Node;

  return template(h, { model: { __rowKey: rowKey, __rowLabel: label } });
}

describe('Water lock: причина червоного рядка словами біля підпису (UAT 08.11, d2)', () => {
  it('рядок з утраченим посиланням: підпис + причина + кнопка «!» на пункт Issues', () => {
    const node = labelCell(slice(), 'R1', 'Sea Water Intake');

    expect(node.cls).toContain('ecr-row-label--attention');
    expect(textOf(find(node, 'ecr-row-label-text'))).toBe('Sea Water Intake');
    const reason = textOf(find(node, 'ecr-row-attention-reason'));
    expect(reason).toContain('grid.rowAttention.orphaned');
    expect(reason).toContain('grid.rowAttention.locked');
    expect(reason).toContain('January–August');
    expect(reason).not.toMatch(/approval|погоджен/i);

    const button = find(node, 'ecr-row-attention-btn');
    expect(button?.tag).toBe('button');
    expect(button?.attrs['data-row-attention-open']).toBe('597:R1');
    expect(String(button?.attrs['aria-label'])).toContain('grid.rowAttention.open');
  });

  it('звичайний рядок — лише підпис, без причини', () => {
    const node = labelCell(slice(), 'R2', 'Fresh Water Intake');

    expect(node.cls).toBe('ecr-row-label');
    expect(find(node, 'ecr-row-attention-reason')).toBeNull();
    expect(textOf(node)).toBe('Fresh Water Intake');
  });

  it('describeColumns: серія з трьох і більше — діапазоном, решта — через кому', () => {
    const dto = slice();

    expect(describeColumns(dto, lockedColumnsOf(dto, 'R1'))).toBe('January–August');
    expect(describeColumns(dto, new Set(['JAN', 'FEB', 'SEP']))).toBe('January, February, September');
    expect(describeColumns(dto, new Set())).toBeNull();
  });

  // ✎ F13-p209 P2-2: причина називає дозвіл і його чинність; спершу «вибрати чинний дозвіл», потім «відкрити місяць».
  // ⛔ Червоне без фіксу: текст був загальний `grid.rowAttention.orphaned` — без номера дозволу й чинності.
  it('Water lock: дозвіл названо з чинністю, порядок — дозвіл, потім відкрити місяць', () => {
    const dto = withPermitColumn(slice());
    const problems = rowLookupProblemsOf(
      dto,
      (registryId, id) =>
        registryId === 50 && id === 7 ? { label: 'KZ35VTE00340032', validFrom: '2026-03-01', validTo: null } : undefined,
      (registryId) => registryId === 50,
    );

    expect([...problems.keys()]).toEqual(['R1']);
    const reason = rowAttentionReason(dto, 'R1', problems.get('R1')) ?? '';
    expect(reason).toContain('grid.rowAttention.permitNotCovering');
    expect(reason).toContain('permit=KZ35VTE00340032');
    expect(reason).toContain('validity=01.03.2026 – …');
    expect(reason).toMatch(/period=[^,)]*2026/);
    expect(reason).not.toContain('grid.rowAttention.orphaned');
    expect(reason.indexOf('permitNotCovering')).toBeLessThan(reason.indexOf('grid.rowAttention.locked'));

    // Той самий текст — у підписі рядка в сітці.
    const columns = gridColumns(
      dto, false, NoLocalFlags, {}, { blocked: new Map(), warning: new Map() },
      new Map(), new Map(), new Map(), new Set(), null, null, new Set(), new Map(), new Map(), problems,
    );
    const template = columns[0]?.cellTemplate as unknown as (create: typeof h, cell: { model: Record<string, unknown> }) => Node;
    const node = template(h, { model: { __rowKey: 'R1', __rowLabel: 'Sea Water Intake' } });
    expect(textOf(find(node, 'ecr-row-attention-reason'))).toContain('permit=KZ35VTE00340032');
    expect(tableAttentionOf(dto, problems).rows[0]?.reason).toContain('permit=KZ35VTE00340032');
  });

  it('чинність: validTo — виключна межа (як на сервері); запис, що покриває місяць, — не проблема', () => {
    expect(describeValidity('2026-03-01', '2027-03-01')).toBe('01.03.2026 – 28.02.2027');
    expect(describeValidity(null, '2026-01-01')).toBe('… – 31.12.2025');
    expect(coversPeriod({ validFrom: '2026-01-31', validTo: null }, 202601)).toBe(true);
    expect(coversPeriod({ validFrom: '2026-02-01', validTo: null }, 202601)).toBe(false);
    expect(coversPeriod({ validFrom: null, validTo: '2026-01-01' }, 202601)).toBe(false);
    expect(coversPeriod({ validFrom: null, validTo: '2026-01-02' }, 202601)).toBe(true);

    const covering = rowLookupProblemsOf(
      withPermitColumn(slice()),
      () => ({ label: 'KZ1', validFrom: '2025-01-01', validTo: null }),
      () => true,
    );
    expect(covering.size).toBe(0);
  });

  it('tableAttentionOf: лише рядки з утраченим посиланням; закриті місяці таблиці', () => {
    const state = tableAttentionOf(slice());

    expect(state.rows.map((row) => row.key)).toEqual(['597:R1']);
    expect(state.rows[0]?.rowLabel).toBe('Sea Water Intake');
    expect(state.lockedMonths).toBe('January–August');
    expect(rowAttentionReason(slice(false), 'R1')).toBeNull();
    expect(tableAttentionOf(slice(false)).rows).toEqual([]);
  });
});
