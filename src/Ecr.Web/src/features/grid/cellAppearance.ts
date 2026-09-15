import type { CSSProperties } from 'react';
import type { CellStyleDto } from '@/api/types';

/**
 * Оформлення колонки в живій сітці — директива
 * `docs/build/directive-registry-lookup-and-cell-style.md`, Частина B, PR
 * B2: стиль, заданий автором шаблону (`ColumnDef.StyleId`), ДОДАЄТЬСЯ до
 * вигляду комірки, а не замінює п'ять семантичних станів (`cellState.ts`).
 *
 * ⛔ `backgroundColor` СВІДОМО не переноситься сюди. Inline `style` завжди
 * переважає над CSS-класом незалежно від специфічності — а всі п'ять станів
 * (`cellState.ts`/`cell-states.css`: dirty/readOnly/calculated/orphaned/
 * rounded) сигналізують САМЕ фоном. Перенести `BackgroundArgb` в inline-стиль
 * означало б ховати індикатор стану під кольором автора шаблону РІВНО тоді,
 * коли директива прямо вимагає протилежного ("стан комірки лишається
 * видимим поверх, одне не повинно ховати інше"). Excel-експорт
 * (`StyleMapper.cs`) і так показує заливку повністю — це обмеження лише
 * ЖИВОЇ сітки, де фон уже зайнятий іншим сигналом.
 *
 * ⚠ `verticalAlign` теж не переноситься: RevoGrid-комірка — звичайний
 * `<div>`, і CSS-властивість `vertical-align` на ньому не робить нічого без
 * `display: table-cell`/flex-розмітки, якої тут немає. Додати її означало б
 * рядок коду, що виглядає як фіча і не робить нічого, — гірше за відсутність.
 */
export function cellAppearanceOf(style: CellStyleDto | null | undefined): CSSProperties | undefined {
  if (style === null || style === undefined) return undefined;

  const css: CSSProperties = {};

  if (style.isBold) css.fontWeight = 'bold';
  if (style.isItalic) css.fontStyle = 'italic';
  if (style.foregroundArgb !== null) css.color = cssColorOfArgb(style.foregroundArgb);
  if (style.wrapText) {
    css.whiteSpace = 'normal';
    css.wordBreak = 'break-word';
  }

  const align = textAlignOf(style.horizontalAlign);
  if (align !== undefined) css.textAlign = align;

  return Object.keys(css).length === 0 ? undefined : css;
}

/** `0` Left, `1` Center, `2` Right, `3` Justify — той самий код, що `StyleMapper.Horizontal`. */
function textAlignOf(horizontalAlign: number | null): CSSProperties['textAlign'] {
  switch (horizontalAlign) {
    case 1:
      return 'center';
    case 2:
      return 'right';
    case 3:
      return 'justify';
    case 0:
      return 'left';
    default:
      return undefined;
  }
}

/** ARGB (`StyleDef.ForegroundArgb`, знак .NET `int`) → `rgb()` — альфа ігнорується, той самий примітив, що й `argbOfHex` (`features/templates/style.ts`), у зворотний бік. */
function cssColorOfArgb(argb: number): string {
  const rgb = argb & 0x00ffffff;
  const r = (rgb >> 16) & 0xff;
  const g = (rgb >> 8) & 0xff;
  const b = rgb & 0xff;

  return `rgb(${String(r)}, ${String(g)}, ${String(b)})`;
}
