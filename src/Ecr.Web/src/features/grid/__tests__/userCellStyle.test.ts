import { describe, expect, it } from 'vitest';
import type { TableSliceDto } from '@/api/types';
import { gridColumns } from '../DocumentGrid';
import { NoLocalFlags } from '../cellState';
import {
  applyStylePatches,
  colourableCells,
  StandardColours,
  ThemeShades,
  userStyleOf,
  withUserStyle,
  chunkPatches,
  isValidPatch,
} from '../userCellStyle';

/**
 * Користувацький колір комірки (L4).
 *
 * ⛔ Мутаційний доказ (руками): брати формат ПЕРЕД умовним форматуванням у
 * `cellProperties` — червоніє «користувач сильніший за правило»; не фільтрувати
 * за `cellPermissions` — червоніє «заборонені комірки не фарбуються»; не
 * віднімати зсув колонки підпису — червоніє «зсув на колонку підпису».
 */
function column(code: string, extra: Partial<TableSliceDto['columns'][number]> = {}): TableSliceDto['columns'][number] {
  return {
    id: 1, code, header: code, dataType: 'Decimal', ordinal: 1, isReadOnly: false, isRequired: false,
    isRequiredByMethodology: false, displayFormat: null, defaultValue: null, lookupRegistryDefId: null,
    unitId: null, unitSymbol: null, style: null, ...extra,
  };
}

function row(rowKey: string, label: string | null = null): TableSliceDto['rows'][number] {
  return { rowKey, ordinal: 1, rowKind: 'Item', label, rowVersion: '0x01', cells: { A: '1', B: '2' }, isOrphaned: false };
}

function slice(extra: Partial<TableSliceDto> = {}): TableSliceDto {
  return {
    tableInstanceId: 1,
    periodKey: 202609,
    columns: [column('A'), column('B')],
    rows: [row('R1'), row('R2')],
    cellPermissions: {},
    cellConfirmations: {},
    ...extra,
  };
}

const noRequired = { blocked: new Map<string, string>(), warning: new Map<string, string>() };

function propsFor(s: TableSliceDto, rowKey: string, code = 'A') {
  const found = gridColumns(s, false, NoLocalFlags, {}, noRequired).find((c) => c.prop === code);
  if (found === undefined || typeof found.cellProperties !== 'function') throw new Error('Немає колонки');

  return found.cellProperties({ model: { __rowKey: rowKey } } as never) ?? {};
}

describe('шар користувацького кольору в gridColumns', () => {
  it('заливка і колір тексту: клас, змінна й атрибут', () => {
    const props = propsFor(slice({ cellStyles: { 'R1:A': { backgroundHex: '#ffff00', foregroundHex: '#ff0000', isBold: false } } }), 'R1');

    expect(props.class).toContain('ecr-cell-filled');
    expect(props['data-user-style']).toBe('true');
    expect((props.style as Record<string, string>)['--ecr-cell-fill']).toBe('#ffff00');
  });

  it('умовне форматування перекриває користувацьке', () => {
    const s = slice({
      cellFormats: { 'R1:A': { backgroundHex: '#00ff00', foregroundHex: null, isBold: true } },
      cellStyles: { 'R1:A': { backgroundHex: '#ffff00', foregroundHex: null, isBold: false } },
    });
    const style = propsFor(s, 'R1').style as Record<string, string>;

    expect(style['--ecr-cell-fill']).toBe('#00ff00');
    expect(style.fontWeight).toBe('bold');
  });

  it('лише своя комірка', () => {
    const props = propsFor(slice({ cellStyles: { 'R1:A': { backgroundHex: '#ffff00', foregroundHex: null, isBold: false } } }), 'R2');

    expect(props).not.toHaveProperty('data-user-style');
  });

  it('зріз без cellStyles — як і був', () => {
    expect(propsFor(slice(), 'R1')).not.toHaveProperty('data-user-style');
  });

  it('незбережена комірка лишається dirty і з кольором', () => {
    const s = slice({ cellStyles: { 'R1:A': { backgroundHex: '#ffff00', foregroundHex: null, isBold: false } } });
    const found = gridColumns(s, false, { dirty: new Set(['R1:A']), rounded: new Set() }, {}, noRequired).find((c) => c.prop === 'A');
    const props = (typeof found?.cellProperties === 'function' ? found.cellProperties({ model: { __rowKey: 'R1' } } as never) : {}) ?? {};

    expect(props['data-cell-state']).toBe('dirty');
    expect(props['data-user-style']).toBe('true');
  });
});

describe('userStyleOf / withUserStyle', () => {
  it('порожній запис — немає стилю', () => {
    expect(userStyleOf({ cellStyles: { k: { backgroundHex: null, foregroundHex: null, isBold: false } } }, 'k')).toBeNull();
    expect(userStyleOf({ cellStyles: null }, 'k')).toBeNull();
  });

  it('withUserStyle: жирність — із сервера, але жирність автора не знімається', () => {
    const base = { isBold: true, isItalic: false, foregroundArgb: null, backgroundArgb: null, horizontalAlign: null, verticalAlign: null, wrapText: false };

    expect(withUserStyle(base, { backgroundHex: '#ff0000', foregroundHex: null, isBold: true }).isBold).toBe(true);
  });
});

describe('colourableCells', () => {
  const range = { fromRow: 0, toRow: 1, fromColumn: 0, toColumn: 1 };

  it('усе виділене без заборон', () => {
    expect(colourableCells(slice(), range, 0)).toHaveLength(4);
  });

  it('заборонені комірки не фарбуються, обчислена і лише-читання — фарбуються', () => {
    const s = slice({ cellPermissions: { 'R1:A': 'NoWriteRight', 'R1:B': 'CalculatedCell', 'R2:A': 'ColumnReadOnly' } });
    const keys = colourableCells(s, range, 0).map((c) => `${c.rowKey}:${c.columnCode}`);

    expect(keys).not.toContain('R1:A');
    expect(keys).toEqual(['R1:B', 'R2:A', 'R2:B']);
  });

  it('зсув на колонку підпису', () => {
    const s = slice({ rows: [row('R1', 'Підпис')] });

    expect(colourableCells(s, { fromRow: 0, toRow: 0, fromColumn: 1, toColumn: 1 }, 1)).toEqual([{ rowKey: 'R1', columnCode: 'A' }]);
  });

  it('нічого не виділено — порожньо', () => {
    expect(colourableCells(slice(), null, 0)).toEqual([]);
  });
});

describe('applyStylePatches', () => {
  it('ставить, зливає поля й скидає (null/null видаляє запис)', () => {
    const one = applyStylePatches(slice(), [{ rowKey: 'R1', columnCode: 'A', backgroundHex: '#ffff00' }]);
    expect(one.cellStyles?.['R1:A']).toEqual({ backgroundHex: '#ffff00', foregroundHex: null, isBold: false });

    const two = applyStylePatches(one, [{ rowKey: 'R1', columnCode: 'A', foregroundHex: '#ff0000' }]);
    expect(two.cellStyles?.['R1:A']).toEqual({ backgroundHex: '#ffff00', foregroundHex: '#ff0000', isBold: false });

    // null — «не чіпати» (семантика сервера), а не «скинути».
    const keep = applyStylePatches(two, [{ rowKey: 'R1', columnCode: 'A', backgroundHex: null, foregroundHex: null }]);
    expect(keep.cellStyles).toEqual(two.cellStyles);

    // "" — зняти.
    const three = applyStylePatches(two, [{ rowKey: 'R1', columnCode: 'A', backgroundHex: '', foregroundHex: '' }]);
    expect(three.cellStyles).toEqual({});
  });

  it('жирність із сервера не губиться при зміні кольору', () => {
    const bold = applyStylePatches(slice(), [{ rowKey: 'R1', columnCode: 'A', isBold: true }]);
    const colored = applyStylePatches(bold, [{ rowKey: 'R1', columnCode: 'A', backgroundHex: '#ffff00' }]);

    expect(colored.cellStyles?.['R1:A']?.isBold).toBe(true);
  });
});

describe('порції і перевірка запису', () => {
  it('порції ≤ 1000, порядок збережено', () => {
    const items = Array.from({ length: 2500 }, (_, i) => i);
    const chunks = chunkPatches(items);

    expect(chunks.map((c) => c.length)).toEqual([1000, 1000, 500]);
    expect(chunks.flat()).toEqual(items);
  });

  it('isValidPatch: #rrggbb, "" і null/undefined дозволені, решта — ні', () => {
    const base = { rowKey: 'R1', columnCode: 'A' };

    expect(isValidPatch({ ...base, backgroundHex: '#ffff00', foregroundHex: '' })).toBe(true);
    expect(isValidPatch({ ...base, backgroundHex: null })).toBe(true);
    expect(isValidPatch({ ...base, backgroundHex: 'red' })).toBe(false);
    expect(isValidPatch({ ...base, foregroundHex: '#fff' })).toBe(false);
  });
});

describe('палітра', () => {
  it('стандартні кольори — як в Excel, відтінки — 5 рядків по 10', () => {
    expect(StandardColours).toHaveLength(10);
    expect(StandardColours).toContain('#ffff00');
    expect(ThemeShades).toHaveLength(5);
    expect(ThemeShades.every((r) => r.length === 10 && r.every((h) => /^#[0-9a-f]{6}$/.test(h)))).toBe(true);
    expect(ThemeShades[0]?.[1]).toBe('#cccccc');
  });
});
