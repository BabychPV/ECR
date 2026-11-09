import { describe, expect, it, vi } from 'vitest';
import type { ColumnRegular } from '@revolist/revogrid';
import type { TableSliceDto } from '@/api/types';
import { splitTargetOfEditStart, withPermitMarks } from '../gridPermitMarks';
import { NoPermitMarks, permitGridMarksOf } from '../splitModel';
import { historyOf, seaWaterRow } from './historyFixture';

/**
 * Feature (j) in the grid: j1b two-line permit cell, j2cell «Split needed» / split glyph + total, the calculated-cell
 * tooltip, and Enter/F2 on a shared month cell opening the split instead of the editor. Untouched columns stay the
 * same objects.
 */
vi.mock('@/shared/i18n', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/i18n')>()),
  t: (key: string, params?: Record<string, string | number>) =>
    params === undefined ? key : `${key} ${JSON.stringify(params)}`,
}));

interface Node {
  tag: string;
  props: Record<string, unknown>;
  children: unknown;
}

const h = ((tag: string, props: Record<string, unknown>, children: unknown): Node => ({ tag, props, children })) as never;

function text(node: unknown): string {
  if (typeof node === 'string') return node;
  if (Array.isArray(node)) return node.map(text).join(' ');
  if (typeof node === 'object' && node !== null && 'children' in node) return text((node as Node).children);

  return '';
}

const rowKeyOf = (model: unknown): string => (model as { __rowKey?: string } | undefined)?.__rowKey ?? '';

const slice = {
  columns: [
    { code: 'Permit', dataType: 'Lookup', expression: null },
    { code: 'Feb', dataType: 'Decimal', expression: null },
    { code: 'Total', dataType: 'Calculated', expression: 'Jan+Feb' },
    { code: 'Unit', dataType: 'Text', expression: null },
  ],
} as unknown as TableSliceDto;

const columns: ColumnRegular[] = [
  { prop: 'Permit', cellTemplate: () => 'LOOKUP' },
  { prop: 'Feb', cellTemplate: (_h, props) => `#${String(props.value)}`, cellProperties: () => ({ class: 'ecr-cell-numeric', title: 'own' }) },
  { prop: 'Total' },
  { prop: 'Unit' },
];

function cell(column: ColumnRegular, rowKey: string, value: unknown): { template: unknown; properties: Record<string, unknown> } {
  const props = { model: { __rowKey: rowKey }, value } as never;

  return {
    template: column.cellTemplate?.(h, props),
    properties: (column.cellProperties?.(props) ?? {}) as Record<string, unknown>,
  };
}

describe('withPermitMarks', () => {
  const marked = withPermitMarks(columns, slice, permitGridMarksOf(historyOf(seaWaterRow()), 701, null), rowKeyOf);

  it('j1b: the permit cell of a row with history shows both permits with their dates; other rows as before', () => {
    const shown = text(cell(marked[0]!, 'R1', 11).template);
    expect(shown).toContain('KZ29VCZ14622425 permits.cell.until {"date":"14.02"}');
    expect(shown).toContain('KZ46VCZ14622410 permits.cell.from {"date":"15.02"}');
    expect(cell(marked[0]!, 'R2', 11).template).toBe('LOOKUP');
  });

  it('j2cell: the shared month holding one value shows «Split needed», amber class, hint keeps the own title', () => {
    const { template, properties } = cell(marked[1]!, 'R1', '12.4');

    expect((template as Node).props['data-split-cell']).toBe('needed');
    // F13 p220 re-check P3: a narrow cell cut «Split n…» — the cell says «Split», the full «Split needed» is its label.
    expect(text(template).trim()).toBe('permits.cell.splitShort');
    expect((template as Node).props['aria-label']).toBe('permits.cell.splitNeeded');
    expect(properties['class']).toBe('ecr-cell-numeric ecr-cell-split-needed');
    expect(String(properties['title'])).toMatch(/^own\npermits\.cell\.splitHint/);
    expect(String(properties['title'])).toContain('permits.split.limitsOnTotal');
    expect(cell(marked[1]!, 'R2', '4.2').template).toBe('#4.2');
  });

  it('j2cell: once split — the glyph and the total', () => {
    const month = seaWaterRow().splitMonths[0]!;
    const split = seaWaterRow({
      splitMonths: [{ ...month, columnCodes: [], cells: [{ columnCode: 'Feb', total: '12.4', parts: ['5.8', '6.6'], isSplit: true }] }],
    });
    const done = withPermitMarks(columns, slice, permitGridMarksOf(historyOf(split), 701, null), rowKeyOf);
    const { template, properties } = cell(done[1]!, 'R1', '12.4');

    expect((template as Node).props['data-split-cell']).toBe('split');
    expect(text(template)).toContain('#12.4');
    expect(properties['class']).toBe('ecr-cell-numeric ecr-cell-split-done');
  });

  it('a calculated cell of a row with history explains it is not split; of another row — no note', () => {
    expect(cell(marked[2]!, 'R1', '3').properties['title']).toBe('permits.history.calculatedNotSplit');
    expect(cell(marked[2]!, 'R2', '3').properties['title']).toBeUndefined();
  });

  it('F13 p220 j7: a CALCULATED row (server lists no cell to split) — no «Split needed», the value and «credited to …»', () => {
    const month = seaWaterRow().splitMonths[0]!;
    const calc = seaWaterRow({ splitMonths: [{ ...month, columnCodes: [] }] });
    const marks = permitGridMarksOf(historyOf(calc), 701, null);
    const shown = withPermitMarks(columns, slice, marks, rowKeyOf);
    const { template, properties } = cell(shown[1]!, 'R1', '12.4');

    expect(template).toBe('#12.4');
    expect(String(properties['class'])).not.toContain('ecr-cell-split');
    expect(String(properties['title'])).toContain('permits.cell.credited');
    expect(String(properties['title'])).toContain('KZ46VCZ14622410');
    expect(String(properties['title'])).toContain('28.02.2026');
    expect(
      splitTargetOfEditStart(marks, { prop: 'Feb', model: { __rowKey: 'R1' } }, { documentId: 3, periodKey: 202602, tableInstanceId: 41, tableDefId: 701, sheetCopyId: null }, rowKeyOf),
    ).toBeNull();
  });

  it('untouched columns are the same objects; no history — nothing changes', () => {
    expect(marked[3]).toBe(columns[3]);
    expect(withPermitMarks(columns, slice, NoPermitMarks, rowKeyOf).every((column, index) => column === columns[index])).toBe(true);
  });
});

describe('splitTargetOfEditStart (j2: Enter/F2/typing opens the split)', () => {
  const marks = permitGridMarksOf(historyOf(seaWaterRow()), 701, null);
  const table = { documentId: 3, periodKey: 202602, tableInstanceId: 41, tableDefId: 701, sheetCopyId: null };

  it('the shared month cell gives the split target', () => {
    expect(splitTargetOfEditStart(marks, { prop: 'Feb', model: { __rowKey: 'R1' } }, table, rowKeyOf)).toEqual({
      ...table,
      rowKey: 'R1',
      columnCode: 'Feb',
    });
  });

  it('any other cell opens the normal editor', () => {
    expect(splitTargetOfEditStart(marks, { prop: 'Jan', model: { __rowKey: 'R1' } }, table, rowKeyOf)).toBeNull();
    expect(splitTargetOfEditStart(marks, { prop: 'Feb', model: { __rowKey: 'R2' } }, table, rowKeyOf)).toBeNull();
  });
});
