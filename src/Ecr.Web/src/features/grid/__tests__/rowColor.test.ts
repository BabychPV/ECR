import { renderHook } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { cellAppearanceOf, withExplicitTextColour } from '../cellAppearance';
import { sameExceptCellStyles, useStyleFreeSlice } from '../useStyleFreeSlice';

/**
 * P1 (Land): (1) явний колір тексту людини діє без перевірки контрасту — на залитій і на незалитій комірці;
 * (2) зміна лише `cellStyles` не змінює ідентичність зрізу для колонок/моделі рядків (повне перемальовування сітки).
 */
const orange = { isBold: false, isItalic: false, foregroundArgb: null, backgroundArgb: 0xffffc000 | 0, horizontalAlign: null, verticalAlign: null, wrapText: false };

describe('withExplicitTextColour', () => {
  it('на помаранчевій заливці обраний червоний не підміняється чорним', () => {
    const appearance = cellAppearanceOf({ ...orange, foregroundArgb: 0xffc00000 | 0 });

    // Без явного вибору `#c00000` не проходить 4.5:1 на `#ffc000` — заміна на текст теми (баг із Land).
    expect(appearance?.['--ecr-cell-fill-fg' as keyof typeof appearance]).not.toBe('#c00000');

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
});

describe('useStyleFreeSlice', () => {
  const slice = { tableInstanceId: 1, rows: [{ rowKey: 'R1' }], columns: [{ code: 'C1' }], cellStyles: {} as Record<string, unknown> };

  it('sameExceptCellStyles: різниця лише в кольорах — однаковий зріз', () => {
    expect(sameExceptCellStyles(slice, { ...slice, cellStyles: { 'R1:C1': { backgroundHex: '#ffc000' } } })).toBe(true);
    expect(sameExceptCellStyles(slice, { ...slice, rows: [...slice.rows] })).toBe(false);
  });

  it('повертає ТОЙ САМИЙ об’єкт при зміні лише кольорів і новий — при зміні решти', () => {
    const { result, rerender } = renderHook(({ data }) => useStyleFreeSlice(data), { initialProps: { data: slice } });
    const first = result.current;

    rerender({ data: { ...slice, cellStyles: { 'R1:C1': { backgroundHex: '#ffc000' } } } });
    expect(result.current, 'колір не має міняти ідентичність').toBe(first);

    rerender({ data: { ...slice, rows: [{ rowKey: 'R1' }, { rowKey: 'R2' }] } });
    expect(result.current, 'зміна рядків — новий зріз').not.toBe(first);
  });

  it('undefined лишається undefined', () => {
    const { result } = renderHook(() => useStyleFreeSlice<typeof slice>(undefined));

    expect(result.current).toBeUndefined();
  });
});
