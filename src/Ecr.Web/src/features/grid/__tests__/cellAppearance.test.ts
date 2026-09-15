import { describe, expect, it } from 'vitest';
import type { CellStyleDto } from '@/api/types';
import { cellAppearanceOf } from '../cellAppearance';

/**
 * Директива `docs/build/directive-registry-lookup-and-cell-style.md`,
 * Частина B, PR B2: показ стилю автора шаблону в живій сітці.
 */

function style(overrides: Partial<CellStyleDto> = {}): CellStyleDto {
  return {
    isBold: false,
    isItalic: false,
    foregroundArgb: null,
    backgroundArgb: null,
    horizontalAlign: null,
    verticalAlign: null,
    wrapText: false,
    ...overrides,
  };
}

describe('cellAppearanceOf', () => {
  it('null/undefined стиль — жодного оформлення', () => {
    expect(cellAppearanceOf(null)).toBeUndefined();
    expect(cellAppearanceOf(undefined)).toBeUndefined();
  });

  it('стиль без жодної позначеної властивості — жодного оформлення (не порожній об\'єкт)', () => {
    expect(cellAppearanceOf(style())).toBeUndefined();
  });

  it('IsBold — font-weight: bold', () => {
    expect(cellAppearanceOf(style({ isBold: true }))).toEqual({ fontWeight: 'bold' });
  });

  it('IsItalic — font-style: italic', () => {
    expect(cellAppearanceOf(style({ isItalic: true }))).toEqual({ fontStyle: 'italic' });
  });

  it('обидва одночасно — обидва властивості присутні', () => {
    expect(cellAppearanceOf(style({ isBold: true, isItalic: true }))).toEqual({
      fontWeight: 'bold',
      fontStyle: 'italic',
    });
  });

  it('ForegroundArgb — колір тексту як rgb(), альфа-байт відкинуто', () => {
    // 0xFFRRGGBB, як зберігає `argbOfHex` (`features/templates/style.ts`) і
    // читає `XLColor.FromArgb` на бекенді: альфа завжди 0xFF, тут неважлива.
    const argb = (0xff112233 | 0) as number;
    expect(cellAppearanceOf(style({ foregroundArgb: argb }))?.color).toBe('rgb(17, 34, 51)');
  });

  it('WrapText — white-space: normal і word-break: break-word', () => {
    expect(cellAppearanceOf(style({ wrapText: true }))).toEqual({
      whiteSpace: 'normal',
      wordBreak: 'break-word',
    });
  });

  it.each([
    [0, 'left'],
    [1, 'center'],
    [2, 'right'],
    [3, 'justify'],
  ] as const)('HorizontalAlign %i → textAlign %s', (value, expected) => {
    expect(cellAppearanceOf(style({ horizontalAlign: value }))?.textAlign).toBe(expected);
  });

  it('HorizontalAlign відсутній (null) — textAlign не задається', () => {
    expect(cellAppearanceOf(style({ horizontalAlign: null }))?.textAlign).toBeUndefined();
  });

  // ⛔ Директива: "стан комірки лишається видимим поверх" — фон і
  // вертикальне вирівнювання НЕ переносяться в живу сітку взагалі (див.
  // коментар `cellAppearanceOf`). Мутаційний доказ негативного факту:
  // жодне поле результату не називається ані `backgroundColor`, ані
  // `verticalAlign`, за жодної комбінації вхідних даних.
  it('BackgroundArgb і VerticalAlign НІКОЛИ не потрапляють у результат', () => {
    const full = style({
      isBold: true,
      backgroundArgb: (0xffff0000 | 0) as number,
      verticalAlign: 1,
    });

    const result = cellAppearanceOf(full);

    expect(result).not.toHaveProperty('backgroundColor');
    expect(result).not.toHaveProperty('background');
    expect(result).not.toHaveProperty('verticalAlign');
  });
});
