import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { CalcResultsGrid, RowHeight, type ResultLabels } from '../CalcResultsGrid';
import type { GridItem } from '../model';
import { yearViewFixture } from './fixture';

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

describe('CalcResultsGrid', () => {
  it('розпірки віртуалізації займають усі колонки рядка (вихід, одиниця, 12 місяців, сума)', () => {
    const view = yearViewFixture();
    const row = view.rows[0]!;
    // Досить рядків, щоб після прокрутки була і верхня, і нижня розпірка.
    const items: GridItem[] = [
      { kind: 'band', level: 0, key: 'b0', row },
      ...Array.from({ length: 300 }, (_, i): GridItem => ({ kind: 'output', key: `o${String(i)}`, row })),
    ];

    render(
      <CalcResultsGrid
        view={view}
        items={items}
        collapsed={new Set()}
        labels={labels}
        pickedMonth={null}
        onToggle={() => undefined}
        onMonth={() => undefined}
        onValue={() => undefined}
      />,
    );
    const grid = screen.getByTestId('calc-results-grid');
    fireEvent.scroll(grid, { target: { scrollTop: 100 * RowHeight } });

    const table = grid.querySelector('table')!;
    const header = table.tHead!.rows[0]!;
    expect(width(header)).toBe(15);

    const body = Array.from(table.tBodies[0]!.rows);
    const spacers = body.filter((r) => r.getAttribute('aria-hidden') === 'true');
    expect(spacers).toHaveLength(2);
    for (const r of body) expect(width(r)).toBe(width(header));
  });
});
