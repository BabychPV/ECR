import { describe, expect, it } from 'vitest';
import { draftColumnsFromCsv, guessKind, parseCsvPreview } from '../csvHeader';

/** Заголовок CSV для майстра «Add a table» (PM-08c): розбір, роздільники, вгадування типів. */

describe('parseCsvPreview', () => {
  it('кома: заголовок і рядки даних', () => {
    const preview = parseCsvPreview('Water body,Source,Volume\nLake,A,10.5\nRiver,B,3\n');
    expect(preview.headers).toEqual(['Water body', 'Source', 'Volume']);
    expect(preview.sample).toEqual([
      ['Lake', 'A', '10.5'],
      ['River', 'B', '3'],
    ]);
  });

  it('крапка з комою (Excel у європейській локалі) і табуляція', () => {
    expect(parseCsvPreview('A;B;C\n1;2;3').headers).toEqual(['A', 'B', 'C']);
    expect(parseCsvPreview('A\tB\n1\t2').headers).toEqual(['A', 'B']);
  });

  it('лапки: роздільник і перенос всередині комірки, подвоєні лапки', () => {
    const preview = parseCsvPreview('"Name, full","He said ""hi""",Plain\r\n"a\nb",x,y');
    expect(preview.headers).toEqual(['Name, full', 'He said "hi"', 'Plain']);
    expect(preview.sample).toEqual([['a\nb', 'x', 'y']]);
  });

  it('BOM і порожні рядки на початку не заважають; зайві порожні комірки в кінці відкидаються', () => {
    const preview = parseCsvPreview('﻿\n\nA,B,,\n1,2,,');
    expect(preview.headers).toEqual(['A', 'B']);
  });

  it('порожній файл дає порожній заголовок', () => {
    expect(parseCsvPreview('').headers).toEqual([]);
    expect(parseCsvPreview('\n\n').headers).toEqual([]);
  });

  it('читає лише заголовок і п’ять рядків даних', () => {
    const rows = Array.from({ length: 20 }, (_, i) => `r${String(i)},1`).join('\n');
    expect(parseCsvPreview(`A,B\n${rows}`).sample).toHaveLength(5);
  });
});

describe('guessKind', () => {
  it('числа, дати, так/ні, інакше текст', () => {
    expect(guessKind(['1', '2,5', '-3.1'])).toBe('Decimal');
    expect(guessKind(['2026-01-31', '01.02.2026'])).toBe('Date');
    expect(guessKind(['Yes', 'no'])).toBe('Bool');
    expect(guessKind(['1', 'abc'])).toBe('String');
  });

  it('без значень — текст; пропуски не рахуються', () => {
    expect(guessKind([])).toBe('String');
    expect(guessKind(['', '  '])).toBe('String');
    expect(guessKind(['', '12', ' 1 000 '])).toBe('Decimal');
  });
});

describe('draftColumnsFromCsv', () => {
  it('перший стовпець — ключовий і обов’язковий, решта ні; типи вгадано', () => {
    const columns = draftColumnsFromCsv(
      parseCsvPreview('Water body,Source,Annual volume\nLake,A,10\nRiver,B,3'),
    );
    expect(columns.map((c) => [c.name, c.dataType, c.key, c.required])).toEqual([
      ['Water body', 'String', true, true],
      ['Source', 'String', false, false],
      ['Annual volume', 'Decimal', false, false],
    ]);
  });

  it('ключі форми різні', () => {
    const columns = draftColumnsFromCsv(parseCsvPreview('A,B,C'));
    expect(new Set(columns.map((c) => c.id)).size).toBe(3);
  });
});
