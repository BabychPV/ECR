import { describe, expect, it } from 'vitest';
import { boardColumns, parseDocumentsView } from '../documentsBoard';

/** `UI-40`: стовпці дошки за станом документа (найгірший стан видимих аркушів). */
const d = (id: number, sheetStates: Record<string, string>) => ({ id, sheetStates });

describe('boardColumns', () => {
  it('розкладає за станом у порядку макета і зберігає порядок переліку', () => {
    const columns = boardColumns([
      d(1, { A: 'Draft' }),
      d(2, { A: 'Approved', B: 'Submitted' }),
      d(3, { A: 'Rejected', B: 'Approved' }),
      d(4, { A: 'Approved' }),
      d(5, { A: 'Draft', B: 'Approved' }),
    ]);

    expect(columns.map((column) => [column.id, column.items.map((item) => item.id)])).toEqual([
      ['Draft', [1, 5]],
      ['Submitted', [2]],
      ['Rework', [3]],
      ['Approved', [4]],
    ]);
  });

  it('стан, якого правило не знає (Returned), — у «Returned or rejected»', () => {
    const columns = boardColumns([d(1, { A: 'Returned' })]);

    expect(columns.find((column) => column.id === 'Rework')?.items.map((item) => item.id)).toEqual([1]);
  });

  it('документи без стану (період не обрано) не губляться: окремий стовпець лише тоді', () => {
    expect(boardColumns([d(1, { A: 'Draft' })]).map((column) => column.id)).not.toContain('NoState');

    const columns = boardColumns([d(1, {}), d(2, { A: 'Mystery' })]);
    expect(columns.at(-1)).toMatchObject({ id: 'NoState' });
    expect(columns.at(-1)?.items.map((item) => item.id)).toEqual([1, 2]);
    // Сума по стовпцях — рівно кількість документів сторінки: жоден не зник і не задвоївся.
    expect(columns.reduce((sum, column) => sum + column.items.length, 0)).toBe(2);
  });

  it('лише «Returned or rejected» чекає уваги', () => {
    expect(boardColumns([]).filter((column) => column.attention).map((column) => column.id)).toEqual(['Rework']);
  });
});

describe('parseDocumentsView', () => {
  it('board — лише точне значення; решта — таблиця', () => {
    expect(parseDocumentsView('board')).toBe('board');
    expect(parseDocumentsView(null)).toBe('table');
    expect(parseDocumentsView('all')).toBe('table');
  });
});
