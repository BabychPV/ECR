import { describe, expect, it } from 'vitest';
import { parseClipboard, toClipboard } from '../tsvClipboard';

/**
 * AN-39 / L8-06: Excel кладе комірки з переносом/табом/лапкою в лапках (RFC-4180).
 * Розбір «по \n і \t» ріже таку комірку на кілька рядків - зсув і запис у чужі комірки.
 */
describe('L8-06: TSV з лапками', () => {
  it('перенос у лапках лишається в одній комірці', () => {
    expect(parseClipboard('"a\nb"\t1\nc\t2\n')).toEqual([
      ['a\nb', '1'],
      ['c', '2'],
    ]);
  });

  it('"" усередині = одна лапка; таб у лапках не ділить колонку; CRLF у лапках зберігається як \\n', () => {
    expect(parseClipboard('"Pipe 5"" long"\t"x\ty"\r\n"p\r\nq"\tz\r\n')).toEqual([
      ['Pipe 5" long', 'x\ty'],
      ['p\nq', 'z'],
    ]);
  });

  it('лапка посеред поля і незакрите поле читаються буквально (як раніше)', () => {
    expect(parseClipboard('5" труба\t2\n"abc\t3\n')).toEqual([
      ['5" труба', '2'],
      ['"abc', '3'],
    ]);
  });

  it('порожні комірки й хвіст: останній таб дає порожню колонку, фінальний перенос - не рядок', () => {
    expect(parseClipboard('"a"\t\t"b"\t\n')).toEqual([['a', '', 'b', '']]);
  });

  it('копіювання обгортає поля з \\t \\n \\r " і розбір повертає їх без втрат (round trip)', () => {
    const matrix = [
      ['Boiler\nNo.2', '10'],
      ['a\tb', 'say "hi"'],
      ['plain', ''],
    ];

    const text = toClipboard(matrix);

    expect(text).toBe('"Boiler\nNo.2"\t10\n"a\tb"\t"say ""hi"""\nplain\t\n');
    expect(parseClipboard(text)).toEqual(matrix);
  });
});
