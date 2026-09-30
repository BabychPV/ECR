import { describe, expect, it } from 'vitest';
import type { CellFormatDto, CellStyleDto } from '@/api/types';
import { cellAppearanceClassOf, cellAppearanceOf, withConditionalFormat } from '../cellAppearance';

/** ФВ-2.6/2.7: результат умовного форматування зі зрізу лягає поверх стилю колонки. */

const base: CellStyleDto = {
  isBold: false,
  isItalic: true,
  foregroundArgb: 0x112233,
  backgroundArgb: null,
  horizontalAlign: 2,
  verticalAlign: null,
  wrapText: true,
};

function format(overrides: Partial<CellFormatDto> = {}): CellFormatDto {
  return { backgroundHex: null, foregroundHex: null, isBold: false, ...overrides };
}

describe('withConditionalFormat', () => {
  it('без формату — стиль колонки без змін (те саме посилання)', () => {
    expect(withConditionalFormat(base, undefined)).toBe(base);
    expect(withConditionalFormat(null, null)).toBeNull();
  });

  it('заливка й жирність накладаються, решта стилю колонки лишається', () => {
    const merged = withConditionalFormat(base, format({ backgroundHex: '#ff0000', isBold: true }));

    expect(merged).toMatchObject({
      isBold: true,
      isItalic: true,
      foregroundArgb: 0x112233,
      backgroundArgb: 0xff0000,
      horizontalAlign: 2,
      wrapText: true,
    });
  });

  it('колір тексту правила перекриває колонковий; null — колонковий лишається', () => {
    expect(withConditionalFormat(base, format({ foregroundHex: '#ffffff' }))?.foregroundArgb).toBe(0xffffff);
    expect(withConditionalFormat(base, format())?.foregroundArgb).toBe(0x112233);
  });

  it('без стилю колонки правило дає заливку, яку сітка реально малює', () => {
    const merged = withConditionalFormat(null, format({ backgroundHex: '#ff0000' }));

    expect(cellAppearanceClassOf(merged)).toBe('ecr-cell-filled');
    expect(cellAppearanceOf(merged)).toMatchObject({ '--ecr-cell-fill': '#ff0000' });
  });
});
