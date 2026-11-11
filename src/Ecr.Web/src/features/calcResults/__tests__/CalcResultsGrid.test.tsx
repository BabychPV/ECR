import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { CalcResultsGrid, RowHeight, type ResultLabels } from '../CalcResultsGrid';
import { resolvePage, type GridItem } from '../model';
import { yearRowsFixture, yearSummaryFixture } from './fixture';

const labels: ResultLabels = {
  equipment: (row) => row.outputCode,
  output: (row) => row.outputCode,
  outputText: (row) => row.outputCode,
  unit: () => 't',
  month: (month) => `M${String(month)}`,
};

/** Сумарна ширина рядка в колонках (з урахуванням colSpan). */
function width(row: HTMLTableRowElement): number {
  return Array.from(row.cells).reduce((sum, cell) => sum + cell.colSpan, 0);
}

const summary = yearSummaryFixture();
const table = summary.tables[0]!;
const row = resolvePage(table, yearRowsFixture('10'))[0]!;

function show(items: GridItem[], onMore = vi.fn(), loading: ReadonlySet<string> = new Set()): HTMLElement {
  render(
    <CalcResultsGrid
      months={summary.months}
      year={summary.year}
      items={items}
      collapsed={new Set()}
      open={new Set([table.key])}
      loading={loading}
      labels={labels}
      pickedMonth={null}
      onToggle={() => undefined}
      onToggleTable={() => undefined}
      onMore={onMore}
      onMonth={() => undefined}
      onValue={() => undefined}
    />,
  );
  return screen.getByTestId('calc-results-grid');
}

describe('CalcResultsGrid', () => {
  it('розпірки віртуалізації займають усі колонки рядка (вихід, одиниця, 12 місяців, сума)', () => {
    // Досить рядків, щоб після прокрутки була і верхня, і нижня розпірка.
    const items: GridItem[] = [
      { kind: 'sheet', key: 's', table },
      ...Array.from({ length: 300 }, (_, i): GridItem => ({ kind: 'output', key: `o${String(i)}`, row })),
    ];
    const grid = show(items);
    fireEvent.scroll(grid, { target: { scrollTop: 100 * RowHeight } });

    const tableElement = grid.querySelector('table')!;
    const header = tableElement.tHead!.rows[0]!;
    expect(width(header)).toBe(15);

    const body = Array.from(tableElement.tBodies[0]!.rows);
    const spacers = body.filter((r) => r.getAttribute('aria-hidden') === 'true');
    expect(spacers).toHaveLength(2);
    for (const r of body) expect(width(r)).toBe(width(header));
  });

  it('aria-rowcount і aria-rowindex — по всіх рядках переліку, включно зі смугами й рядком «ще»', () => {
    const items: GridItem[] = [
      { kind: 'sheet', key: 's', table },
      { kind: 'table', key: table.key, table },
      { kind: 'output', key: row.id, row },
      { kind: 'more', key: 'm', table, loaded: 1, total: 3 },
    ];
    const grid = show(items);
    const tableElement = grid.querySelector('table')!;
    expect(tableElement.getAttribute('aria-rowcount')).toBe('5');
    expect(Array.from(tableElement.tBodies[0]!.rows).map((r) => r.getAttribute('aria-rowindex'))).toEqual(['2', '3', '4', '5']);
  });

  it('рядок «ще» у видимій області просить наступну сторінку; поки сторінка читається — лише стан', () => {
    const items: GridItem[] = [
      { kind: 'table', key: table.key, table },
      { kind: 'more', key: 'm', table, loaded: 200, total: 450 },
    ];
    const onMore = vi.fn();
    show(items, onMore);
    expect(onMore).toHaveBeenCalledWith(table.key, 200);
  });

  it('поки сторінка читається, рядок «ще» не просить її вдруге', () => {
    const onMore = vi.fn();
    const grid = show([{ kind: 'more', key: 'm', table, loaded: 0, total: 3 }], onMore, new Set([table.key]));
    expect(onMore).not.toHaveBeenCalled();
    expect(grid.querySelector('[role="status"]')).not.toBeNull();
  });
});
