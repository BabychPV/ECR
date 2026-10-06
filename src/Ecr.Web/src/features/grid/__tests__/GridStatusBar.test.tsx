import { afterEach, describe, expect, it } from 'vitest';
import { act, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import type { TableSliceDto } from '@/api/types';
import { GridStatusBar } from '../GridStatusBar';
import { publishSelection } from '../selectionStore';
import { testTheme } from '@/test/render';

/** UI-23: рядок стану під сіткою. */

const slice = {
  tableInstanceId: 9,
  periodKey: 202610,
  columns: [
    { code: 'A', header: 'A', dataType: 'Decimal' },
    { code: 'B', header: 'B', dataType: 'Decimal' },
  ],
  rows: [{ rowKey: 'R1' }, { rowKey: 'R2' }],
} as unknown as TableSliceDto;

const rows = [
  { A: '1', B: '2' },
  { A: '3', B: '' },
];

function show(): void {
  render(
    <MantineProvider theme={testTheme}>
      <GridStatusBar tableInstanceId={9} periodKey={202610} slice={slice} columns={[{ prop: 'A' }, { prop: 'B' }]} rows={rows} />
    </MantineProvider>,
  );
}

afterEach(() => {
  publishSelection(9, 202610, null);
});

describe('GridStatusBar', () => {
  it('без діапазону — розмір таблиці', () => {
    show();

    expect(screen.getByTestId('grid-status-size').textContent).toBe('⟦grid.status.size (rows=2, columns=2)⟧');
  });

  it('діапазон чисел — Average · Count · Sum; виділення іншого зрізу не чіпає', () => {
    show();

    act(() => {
      publishSelection(9, 202611, { fromRow: 0, toRow: 1, fromColumn: 0, toColumn: 1 });
    });
    expect(screen.queryByTestId('grid-status-sum')).toBeNull();

    act(() => {
      publishSelection(9, 202610, { fromRow: 0, toRow: 1, fromColumn: 0, toColumn: 1 });
    });
    expect(screen.getByTestId('grid-status-count').textContent).toContain('3');
    expect(screen.getByTestId('grid-status-sum').textContent).toContain('6');
    expect(screen.getByTestId('grid-status-average').textContent).toContain('2');
  });
});
