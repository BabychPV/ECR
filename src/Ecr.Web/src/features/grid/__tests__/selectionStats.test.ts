import { describe, expect, it } from 'vitest';
import type { TableSliceDto } from '@/api/types';
import { selectionStats } from '../selectionStats';

/** UI-23: Count / Sum / Average виділеного діапазону (рядок стану сітки). */

const slice = {
  tableInstanceId: 1,
  periodKey: 202610,
  columns: [
    { code: 'A', header: 'A', dataType: 'Decimal' },
    { code: 'B', header: 'B', dataType: 'Decimal' },
  ],
  rows: [{ rowKey: 'R1' }, { rowKey: 'R2' }, { rowKey: 'R3' }],
} as unknown as TableSliceDto;

// Колонка 0 — підпис рядка, як у сітці з підписами.
const columns = [{ prop: '__label' }, { prop: 'A' }, { prop: 'B' }];

const rows = [
  { __label: 'Row 1', A: '0.1', B: '10' },
  { __label: 'Row 2', A: '0.2', B: '' },
  { __label: 'Row 3', A: '12345678901234567.0000000000000001', B: 'н/д' },
];

describe('selectionStats', () => {
  it('одна комірка — підсумку немає (рядок показує розмір таблиці)', () => {
    expect(selectionStats({ slice, columns, rows, range: { fromRow: 0, toRow: 0, fromColumn: 1, toColumn: 1 } })).toBeNull();
  });

  it('сума десяткова без похибки Number; порожні й нечислові не беруть участі; підпис пропущено', () => {
    const stats = selectionStats({ slice, columns, rows, range: { fromRow: 0, toRow: 1, fromColumn: 0, toColumn: 2 } });

    // 0.1 + 0.2 + 10 — рівно 10.3, а не 10.300000000000001.
    expect(stats).toEqual({ count: 3, sum: '10.3', average: '3.43333' });
  });

  it('великі decimal(25,16) — точно', () => {
    const stats = selectionStats({ slice, columns, rows, range: { fromRow: 1, toRow: 2, fromColumn: 1, toColumn: 1 } });

    expect(stats?.sum).toBe('12345678901234567.2000000000000001');
    expect(stats?.count).toBe(2);
  });

  it('незбережена правка перекриває модель', () => {
    const pending = new Map([['R2:B', { rowKey: 'R2', columnCode: 'B', value: '5' }]]);
    const stats = selectionStats({ slice, columns, rows, range: { fromRow: 0, toRow: 1, fromColumn: 2, toColumn: 2 }, pending });

    expect(stats).toEqual({ count: 2, sum: '15', average: '7.5' });
  });

  it('середнє округлюється половиною від нуля', () => {
    const negative = [{ A: '-1' }, { A: '-1' }, { A: '-0.0001' }];
    const stats = selectionStats({
      slice,
      columns: [{ prop: 'A' }],
      rows: negative,
      range: { fromRow: 0, toRow: 2, fromColumn: 0, toColumn: 0 },
    });

    // −2.0001 / 3 = −0.66670000… → −0.66670000 при масштабі 4 + 4.
    expect(stats?.sum).toBe('-2.0001');
    expect(stats?.average).toBe('-0.6667');
  });
});
