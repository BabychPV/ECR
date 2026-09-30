import { describe, expect, it } from 'vitest';
import type { CellStyleDto } from '@/api/types';
import { AA, contrast } from '@/shared/theme/contrast';
import { cellAppearanceClassOf, cellAppearanceOf, DarkSurface } from '../cellAppearance';

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

  it('ForegroundArgb — колір тексту змінною під КОЖНУ тему, альфа-байт відкинуто', () => {
    // 0xFFRRGGBB, як зберігає `argbOfHex` (`features/templates/style.ts`) і
    // читає `XLColor.FromArgb` на бекенді: альфа завжди 0xFF, тут неважлива.
    const argb = (0xff112233 | 0) as number;
    const result = cellAppearanceOf(style({ foregroundArgb: argb })) as Record<string, unknown>;

    expect(result['--ecr-cell-fg-light']).toBe('#112233');
    // ⛔ `X-10`: inline `color` більше не ставиться — він не знав теми.
    expect(result).not.toHaveProperty('color');
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

  // ⛔ Директива: "стан комірки лишається видимим поверх" — тому ні фон, ні
  // колір не йдуть inline-властивостями, які переважили б фон стану; заливка
  // йде ЗМІННОЮ, і її застосовує лише комірка без стану (`cellEditors.css`).
  it('BackgroundArgb — змінна заливки, а не inline backgroundColor; VerticalAlign — ніколи', () => {
    const full = style({
      isBold: true,
      backgroundArgb: (0xffff0000 | 0) as number,
      verticalAlign: 1,
    });

    const result = cellAppearanceOf(full) as Record<string, unknown>;

    expect(result).not.toHaveProperty('backgroundColor');
    expect(result).not.toHaveProperty('background');
    expect(result).not.toHaveProperty('verticalAlign');
    expect(result['--ecr-cell-fill']).toBe('#ff0000');
  });
});

describe('X-10: колір автора читається в обох темах, заливка — з читабельним текстом', () => {
  /**
   * ⛔ Живцем на стенді: колір тексту колонки `rgb(26,26,26)` у темній темі
   * стояв на `#242424` — контраст ~1.1:1, текст невидимий.
   */
  it('темний колір автора — лише у світлій темі; у темній діє колір теми', () => {
    const result = cellAppearanceOf(style({ foregroundArgb: (0xff1a1a1a | 0) as number })) as Record<string, unknown>;

    expect(result['--ecr-cell-fg-light']).toBe('#1a1a1a');
    expect(result).not.toHaveProperty('--ecr-cell-fg-dark');
    expect(contrast('#1a1a1a', DarkSurface)).toBeLessThan(AA.text);
  });

  it('світлий колір автора — лише в темній темі', () => {
    const result = cellAppearanceOf(style({ foregroundArgb: (0xffffeb3b | 0) as number })) as Record<string, unknown>;

    expect(result).not.toHaveProperty('--ecr-cell-fg-light');
    expect(result['--ecr-cell-fg-dark']).toBe('#ffeb3b');
  });

  it('текст на заливці — авторський, якщо читається; інакше чорний чи білий, що контрастніший', () => {
    const yellow = cellAppearanceOf(style({ backgroundArgb: (0xffffff00 | 0) as number })) as Record<string, unknown>;
    expect(yellow['--ecr-cell-fill-fg']).toBe('#000000');

    const navy = cellAppearanceOf(
      style({ backgroundArgb: (0xff000080 | 0) as number, foregroundArgb: (0xff1a1a1a | 0) as number }),
    ) as Record<string, unknown>;
    expect(navy['--ecr-cell-fill-fg']).toBe('#ffffff');

    const readable = cellAppearanceOf(
      style({ backgroundArgb: (0xffffff00 | 0) as number, foregroundArgb: (0xff000080 | 0) as number }),
    ) as Record<string, unknown>;
    expect(readable['--ecr-cell-fill-fg']).toBe('#000080');

    for (const result of [yellow, navy, readable]) {
      expect(contrast(String(result['--ecr-cell-fill-fg']), String(result['--ecr-cell-fill']))).toBeGreaterThanOrEqual(AA.text);
    }
  });

  it('класи, якими CSS застосовує змінні', () => {
    expect(cellAppearanceClassOf(style({ isBold: true }))).toBeNull();
    expect(cellAppearanceClassOf(style({ foregroundArgb: 1 }))).toBe('ecr-cell-styled');
    expect(cellAppearanceClassOf(style({ foregroundArgb: 1, backgroundArgb: 2 }))).toBe('ecr-cell-styled ecr-cell-filled');
  });
});
