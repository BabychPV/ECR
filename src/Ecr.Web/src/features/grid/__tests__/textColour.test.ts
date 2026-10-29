import { describe, expect, it } from 'vitest';
import { cellAppearanceOf, withExplicitTextColour } from '../cellAppearance';

/**
 * P1 (Land): явний колір тексту людини діє без перевірки контрасту — на залитій і на незалитій комірці;
 * комірка БЕЗ явного кольору зберігає автоконтраст (bestTextOn).
 */
const orange = { isBold: false, isItalic: false, foregroundArgb: null, backgroundArgb: 0xffffc000 | 0, horizontalAlign: null, verticalAlign: null, wrapText: false };

describe('withExplicitTextColour', () => {
  it('на помаранчевій заливці обраний червоний не підміняється чорним', () => {
    const appearance = cellAppearanceOf({ ...orange, foregroundArgb: 0xffc00000 | 0 });

    // Без явного вибору `#c00000` не проходить 4.5:1 на `#ffc000` — заміна на текст теми (баг із Land).
    expect((appearance as Record<string, string>)['--ecr-cell-fill-fg']).not.toBe('#c00000');

    const explicit = withExplicitTextColour(appearance, '#c00000', null) as Record<string, string>;

    expect(explicit['--ecr-cell-fill-fg']).toBe('#c00000');
    expect(explicit['--ecr-cell-fg-light']).toBe('#c00000');
    expect(explicit['--ecr-cell-fg-dark']).toBe('#c00000');
    expect(explicit['--ecr-cell-fill']).toBe('#ffc000');
  });

  it('без заливки колір теж явний (темна тема: темно-червоний не відкидається за контрастом)', () => {
    const explicit = withExplicitTextColour(undefined, '#c00000', undefined) as Record<string, string>;

    expect(explicit['--ecr-cell-fg-dark']).toBe('#c00000');
    expect(explicit['--ecr-cell-fg-light']).toBe('#c00000');
  });

  it('нічого не чіпає без вибору людини й поступається умовному форматуванню', () => {
    const base = cellAppearanceOf(orange);

    expect(withExplicitTextColour(base, null, null)).toBe(base);
    expect(withExplicitTextColour(base, '', null)).toBe(base);
    expect(withExplicitTextColour(base, '#c00000', '#0000ff')).toBe(base);
  });

  it('без явного кольору заливка зберігає автоконтраст (текст не збігається із заливкою)', () => {
    const appearance = cellAppearanceOf(orange) as Record<string, string>;

    expect(appearance['--ecr-cell-fill']).toBe('#ffc000');
    expect(appearance['--ecr-cell-fill-fg']).toMatch(/^#/);
    expect(appearance['--ecr-cell-fill-fg']).not.toBe('#ffc000');
  });
});
