import { describe, expect, it } from 'vitest';
import { MaxPastedRows, parseLocaleDate, parseLocaleNumber, parsePastedRows, type PasteColumn } from '../pasteRows';

/**
 * Мутаційний доказ (вручну, відновлено): `parseLocaleNumber` без заміни коми на крапку валив
 * «десяткова кома»; без `replace(SpaceLike, '')` — «пробіли як тисячі»; `pastedBool` замість
 * порожнього → `null` (порожня «Так/Ні» ставала `false`) валило «порожня комірка — null».
 */
const columns: PasteColumn[] = [
  { code: 'Substance', type: 'link' },
  { code: 'Amount', type: 'number' },
  { code: 'Start', type: 'date' },
  { code: 'Active', type: 'bool' },
  { code: 'Note', type: 'text' },
];

describe('parseLocaleNumber', () => {
  it.each([
    ['12', 12],
    ['12.5', 12.5],
    ['12,5', 12.5],
    ['-0,5', -0.5],
    ['+3', 3],
    ['.5', 0.5],
    ['0', 0],
    ['1 234,5', 1234.5],
    ['1 234,5', 1234.5],
    ['1 234,5', 1234.5],
    ['1.234,5', 1234.5],
    ['1,234.5', 1234.5],
    ['1,234', 1.234],
    ['1,234,567', 1234567],
    ['1.234.567', 1234567],
    ['1,76E-05', 0.0000176],
    ['1.76E-05', 0.0000176],
    ['  7  ', 7],
  ])('«%s» → %s', (text, expected) => {
    expect(parseLocaleNumber(text)).toBeCloseTo(expected, 12);
  });

  it.each(['', '   ', 'abc', '12abc', '1,2,3x', '1.2.3', '12,34,5', '--1', '1,,2', ',', '.', '1e', 'NaN', 'Infinity'])(
    '«%s» — не число',
    (text) => {
      expect(parseLocaleNumber(text)).toBeNull();
    },
  );
});

describe('parseLocaleDate', () => {
  it.each([
    ['2026-01-05', '2026-01-05'],
    ['2026-1-5', '2026-01-05'],
    ['05.01.2026', '2026-01-05'],
    ['5.1.2026', '2026-01-05'],
    ['29.02.2024', '2024-02-29'],
    ['  2026-12-31 ', '2026-12-31'],
  ])('«%s» → %s', (text, expected) => {
    expect(parseLocaleDate(text)).toBe(expected);
  });

  it.each(['', 'yesterday', '31.04.2026', '29.02.2025', '2026-13-01', '2026-02-30', '01/05/2026', '5.1.26'])(
    '«%s» — не дата',
    (text) => {
      expect(parseLocaleDate(text)).toBeNull();
    },
  );
});

describe('parsePastedRows', () => {
  it('рядок тексту = рядок таблиці, клітинки — за порядком стовпців', () => {
    const result = parsePastedRows('Soot\t1,5\t2026-01-01\tYes\thello\nXylene\t2\t05.01.2026\tNo\t', columns);

    expect(result.rows.map((r) => r.values)).toEqual([
      { Substance: 'Soot', Amount: 1.5, Start: '2026-01-01', Active: true, Note: 'hello' },
      { Substance: 'Xylene', Amount: 2, Start: '2026-01-05', Active: false, Note: null },
    ]);
    expect(result.issues).toEqual([]);
    expect(result.extraCells).toBe(0);
  });

  it('Yes/No приймає да / так / иә / 1 / true і нет / ні / 0 / false', () => {
    const bools = (text: string) =>
      parsePastedRows(text, [{ code: 'b', type: 'bool' }]).rows.map((r) => r.values.b);

    expect(bools('yes\nYES\nда\nтак\nиә\n1\ntrue\nY')).toEqual([true, true, true, true, true, true, true, true]);
    expect(bools('no\nнет\nні\nжоқ\n0\nfalse\nN')).toEqual([false, false, false, false, false, false, false]);
  });

  it('число з десятковою комою та пробілами-тисячами', () => {
    const result = parsePastedRows('1 234,56\n 0,001 \n-2', [{ code: 'n', type: 'number' }]);
    expect(result.rows.map((r) => r.values.n)).toEqual([1234.56, 0.001, -2]);
  });

  it('порожні рядки (в т.ч. хвостовий перенос Excel) пропускаються і не збивають нумерацію', () => {
    const result = parsePastedRows('A\t1\n\n   \t\nB\tx\n', [
      { code: 'name', type: 'text' },
      { code: 'n', type: 'number' },
    ]);
    expect(result.rows).toHaveLength(2);
    expect(result.issues).toEqual([{ row: 2, column: 'n', text: 'x', reason: 'number' }]);
  });

  it('CRLF і самотній CR як перенос рядка', () => {
    const result = parsePastedRows('a\r\nb\rc', [{ code: 't', type: 'text' }]);
    expect(result.rows.map((r) => r.values.t)).toEqual(['a', 'b', 'c']);
  });

  it('порожня комірка — null для будь-якого типу, а не 0 / false / ""', () => {
    const { rows } = parsePastedRows('\t\t\t\tx', columns);
    expect(rows[0]?.values).toEqual({ Substance: null, Amount: null, Start: null, Active: null, Note: 'x' });
  });

  it('нерозпізнане значення стає null і потрапляє в issues, рядок усе одно додається', () => {
    const result = parsePastedRows('Soot\tabc\tyesterday\tmaybe\tz', columns);
    expect(result.rows).toHaveLength(1);
    expect(result.rows[0]?.values).toMatchObject({ Amount: null, Start: null, Active: null, Note: 'z' });
    expect(result.issues).toEqual([
      { row: 1, column: 'Amount', text: 'abc', reason: 'number' },
      { row: 1, column: 'Start', text: 'yesterday', reason: 'date' },
      { row: 1, column: 'Active', text: 'maybe', reason: 'bool' },
    ]);
  });

  it('менше клітинок, ніж стовпців, — решта null; більше — зайві рахуються й відкидаються', () => {
    const two: PasteColumn[] = [
      { code: 'a', type: 'text' },
      { code: 'b', type: 'text' },
    ];
    const result = parsePastedRows('x\ny\tz\tq\tw', two);
    expect(result.rows.map((r) => r.values)).toEqual([
      { a: 'x', b: null },
      { a: 'y', b: 'z' },
    ]);
    expect(result.extraCells).toBe(2);
  });

  it('текстові клітинки обрізаються по краях, внутрішні пробіли лишаються', () => {
    const { rows } = parsePastedRows('  a  b  \t', [
      { code: 'a', type: 'text' },
      { code: 'b', type: 'text' },
    ]);
    expect(rows[0]?.values).toEqual({ a: 'a  b', b: null });
  });

  it('порожній вхід і вхід лише з порожніх рядків — нуль рядків', () => {
    expect(parsePastedRows('', columns).rows).toEqual([]);
    expect(parsePastedRows('\n\t\t\n', columns).rows).toEqual([]);
  });

  describe('стеля рядків', () => {
    const one: PasteColumn[] = [{ code: 'Note', type: 'text' }];
    const block = (count: number): string => Array.from({ length: count }, (_, i) => `r${i + 1}`).join('\n');

    it('рівно MaxPastedRows — усі рядки, без запису про перевищення', () => {
      const result = parsePastedRows(block(MaxPastedRows), one);
      expect(result.rows).toHaveLength(MaxPastedRows);
      expect(result.issues).toEqual([]);
    });

    it('на один більше — рядки до стелі лишаються, а перевищення названо в issues (не мовчки)', () => {
      const result = parsePastedRows(block(MaxPastedRows + 1), one);
      expect(result.rows).toHaveLength(MaxPastedRows);
      expect(result.rows[MaxPastedRows - 1]?.values.Note).toBe(`r${MaxPastedRows}`);
      expect(result.issues).toEqual([{ row: MaxPastedRows + 1, column: '', text: '', reason: 'limit' }]);
    });

    it('багато зайвих рядків — один запис про перевищення; порожні рядки до стелі не рахуються', () => {
      const result = parsePastedRows(`\n\n${block(MaxPastedRows + 50)}`, one);
      expect(result.rows).toHaveLength(MaxPastedRows);
      expect(result.issues.filter((issue) => issue.reason === 'limit')).toHaveLength(1);
    });
  });

  it('без стовпців рядок усе одно створюється (порожній), а клітинки — зайві', () => {
    const result = parsePastedRows('a\tb', []);
    expect(result.rows).toHaveLength(1);
    expect(result.extraCells).toBe(2);
  });
});
