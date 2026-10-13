import { describe, expect, it } from 'vitest';
import type { ColumnDto, RowDto, TableSliceDto } from '@/api/types';
import { normalizeDecimal } from '@/shared/format';
import { cellDisplay, cellDisplayMemo } from '../cellValue';
import { columnTotals } from '../gridTotals';
import { decimalTextOf } from '../rounding';

/**
 * `P2` (T3_00, 232 колонки): швидкі шляхи розбору десяткового й кеш підсумків не мають
 * права змінити жодного результату. Еталон — повільний шлях: той самий текст в
 * обрамленні пробілами не збігається зі швидким regex-ом і йде через `readNumber`.
 */

function rng(seed: number): () => number {
  let x = seed;

  return () => {
    x = (x * 1664525 + 1013904223) % 4294967296;

    return x / 4294967296;
  };
}

function randomText(next: () => number): string {
  const pick = (alphabet: string, max: number): string => {
    const length = 1 + Math.floor(next() * max);
    let out = '';
    for (let i = 0; i < length; i++) out += alphabet[Math.floor(next() * alphabet.length)];

    return out;
  };

  const sign = ['', '', '', '-', '+'][Math.floor(next() * 5)] ?? '';
  const int = next() < 0.5 ? pick('0123456789', 5) : pick('0', 2);
  const kind = next();

  if (kind < 0.35) return sign + int;
  if (kind < 0.8) return `${sign}${int}.${pick('0123456789', 5)}`;
  if (kind < 0.9) return `${sign}${int},${pick('0123456789', 4)}`;

  return `${sign}${int}.${pick('0123456789', 3)}.${pick('0123456789', 3)}`;
}

describe('швидкі шляхи десяткового = повільний шлях', () => {
  it('decimalTextOf: випадкові рядки в кількох локалях', () => {
    const next = rng(20261013);

    for (const locale of ['en', 'ru', 'kk', 'de-DE']) {
      for (let i = 0; i < 4000; i++) {
        const text = randomText(next);

        expect(decimalTextOf(text, locale), `${locale} «${text}»`).toBe(decimalTextOf(` ${text} `, locale));
      }
    }
  });

  it('decimalTextOf: межові записи (-0, хвостові нулі, 1,234-подібні)', () => {
    for (const locale of ['en', 'ru', 'de-DE']) {
      for (const text of ['0', '-0', '0.0', '-0.000', '5.0000000000', '1.234', '1.500', '12.345', '1234.567', '0.123', '007', '-007.50']) {
        expect(decimalTextOf(text, locale), `${locale} «${text}»`).toBe(decimalTextOf(` ${text} `, locale));
      }
    }
  });

  it('normalizeDecimal: випадкові рядки', () => {
    const next = rng(7);

    for (let i = 0; i < 8000; i++) {
      const text = randomText(next);

      expect(normalizeDecimal(text), `«${text}»`).toBe(normalizeDecimal(` ${text} `));
    }
  });

  it('normalizeDecimal: канонічне повертається без змін', () => {
    for (const text of ['0', '5', '-5', '0.5', '-0.5', '12.3456789012345678']) expect(normalizeDecimal(text)).toBe(text);
    for (const [input, expected] of [['-0', '0'], ['5.0', '5'], ['05', '5'], ['0.50', '0.5'], ['+1', '1']] as const) {
      expect(normalizeDecimal(input)).toBe(expected);
    }
  });
});

const column = (code: string, dataType = 'Decimal'): ColumnDto => ({
  id: 1, code, header: code, dataType, ordinal: 1, isReadOnly: false, isRequired: false,
  isRequiredByMethodology: false, displayFormat: null, defaultValue: null, lookupRegistryDefId: null, unitId: null, unitSymbol: null,
} as ColumnDto);

const rowOf = (rowKey: string, cells: Record<string, unknown>): RowDto => ({
  rowKey, ordinal: 1, rowKind: 'Item', label: null, rowVersion: '0x01', cells, isOrphaned: false,
});

function sliceOf(columns: ColumnDto[], rows: RowDto[]): TableSliceDto {
  return { tableInstanceId: 1, periodKey: 202609, columns, rows, cellPermissions: {}, cellConfirmations: {} };
}

describe('columnTotals: підсумки незайманих колонок кешуються, змінені — ні', () => {
  const slice = sliceOf(
    [column('A'), column('B'), column('T', 'String')],
    [rowOf('r1', { A: '1.5', B: '10' }), rowOf('r2', { A: '2.25', B: '20' })],
  );

  it('правка в A не псує B і не лишає стару суму в A', () => {
    const before = columnTotals(slice, {});
    expect(before.get('A')).toEqual({ sum: '3.75', count: 2 });
    expect(before.get('B')).toEqual({ sum: '30', count: 2 });

    const pending = new Map([['r1:A', { rowKey: 'r1', columnCode: 'A', value: '10' }]]);
    const edited = columnTotals(slice, { pending });
    expect(edited.get('A')).toEqual({ sum: '12.25', count: 2 });
    expect(edited.get('B')).toEqual({ sum: '30', count: 2 });

    // правку скасовано — повертається сума зрізу, а не сума правки
    expect(columnTotals(slice, {}).get('A')).toEqual({ sum: '3.75', count: 2 });
  });

  it('override у B перераховує лише B', () => {
    const overrides = new Map<string, unknown>([['r2:B', '5']]);
    const totals = columnTotals(slice, { overrides });

    expect(totals.get('B')).toEqual({ sum: '15', count: 2 });
    expect(totals.get('A')).toEqual({ sum: '3.75', count: 2 });
  });

  it('порожня колонка не потрапляє в мапу навіть з кешу', () => {
    const empty = sliceOf([column('A')], [rowOf('r1', { A: '' })]);

    expect(columnTotals(empty, {}).has('A')).toBe(false);
    expect(columnTotals(empty, {}).has('A')).toBe(false);
  });
});

describe('cellDisplayMemo = cellDisplay', () => {
  it('та сама відповідь, у тому числі для не-рядків і повторів', () => {
    const col = { ...column('A'), scale: 2 } as ColumnDto;
    const memo = cellDisplayMemo(col);

    for (const value of ['1234.5', '1234.5', 'н/д', '', null, undefined, 7, '0.10']) {
      expect(memo(value)).toBe(cellDisplay(value, col));
    }
  });
});
