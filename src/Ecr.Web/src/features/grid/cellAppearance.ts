import type { CSSProperties } from 'react';
import type { CellStyleDto } from '@/api/types';
import { AA, contrast } from '@/shared/theme/contrast';

/**
 * Оформлення колонки в живій сітці — директива
 * `docs/build/directive-registry-lookup-and-cell-style.md`, Частина B, PR
 * B2: стиль, заданий автором шаблону (`ColumnDef.StyleId`), ДОДАЄТЬСЯ до
 * вигляду комірки, а не замінює п'ять семантичних станів (`cellState.ts`).
 *
 * ✎ `X-10`. Колір тексту й заливка автора йдуть не inline-властивостями
 * `color`/`backgroundColor`, а ЗМІННИМИ (`--ecr-cell-*`), які застосовують
 * правила `cellEditors.css`. Причини дві, і обидві виміряні на стенді:
 *
 *  1. Колір тексту автора підбирався під світлий Excel. Inline `color:
 *     rgb(26,26,26)` лишався таким і в темній темі — на поверхні `#242424`,
 *     тобто текст був невидимий (контраст ~1.1:1). Тепер колір рахується
 *     ДВІЧІ — під світлу й під темну поверхню — і там, де авторський колір не
 *     дає контрасту `AA.text` (4.5:1), його замінює колір тексту теми.
 *  2. Заливку автора не було видно зовсім: inline `backgroundColor` завжди
 *     переважає CSS-клас, а всі п'ять станів сигналізують саме фоном — тож її
 *     свідомо не переносили. Тепер вона застосовується правилом
 *     `:not([data-cell-state])`: комірка без стану має заливку автора, а щойно
 *     з'являється стан (незбережена, лише читання, обчислена…) — фон знову
 *     належить стану, і індикатор не ховається під кольором автора.
 *
 * ⚠ На заливці текст рахується окремо (`--ecr-cell-fill-fg`): заливка однакова
 * в обох темах, і текст теми (світлий у темній) на жовтій заливці був би тим
 * самим невидимим текстом, лише навпаки.
 *
 * ⚠ `verticalAlign` не переноситься: RevoGrid-комірка — звичайний `<div>`, і
 * `vertical-align` на ньому не робить нічого без `display: table-cell`/flex.
 */
export function cellAppearanceOf(style: CellStyleDto | null | undefined): CSSProperties | undefined {
  if (style === null || style === undefined) return undefined;

  const css: CSSProperties & Record<`--${string}`, string> = {};

  if (style.isBold) css.fontWeight = 'bold';
  if (style.isItalic) css.fontStyle = 'italic';

  const foreground = style.foregroundArgb === null ? null : hexOfArgb(style.foregroundArgb);
  const fill = style.backgroundArgb === null ? null : hexOfArgb(style.backgroundArgb);

  if (foreground !== null) {
    const onLight = readableOn(foreground, LightSurface);
    const onDark = readableOn(foreground, DarkSurface);

    // ⚠ Нечитабельний на поверхні колір не задається зовсім: правило CSS тоді
    // бере колір тексту теми (`--ecr-grid-text`), а не вгадує третій.
    if (onLight !== null) css['--ecr-cell-fg-light'] = onLight;
    if (onDark !== null) css['--ecr-cell-fg-dark'] = onDark;
  }

  if (fill !== null) {
    css['--ecr-cell-fill'] = fill;
    css['--ecr-cell-fill-fg'] = (foreground === null ? null : readableOn(foreground, fill)) ?? bestTextOn(fill);
  }

  if (style.wrapText) {
    css.whiteSpace = 'normal';
    css.wordBreak = 'break-word';
  }

  const align = textAlignOf(style.horizontalAlign);
  if (align !== undefined) css.textAlign = align;

  return Object.keys(css).length === 0 ? undefined : css;
}

/**
 * Класи, якими `cellEditors.css` застосовує змінні `cellAppearanceOf` (`X-10`).
 *
 * @returns `null` — колір і заливку автор не задавав.
 */
export function cellAppearanceClassOf(style: CellStyleDto | null | undefined): string | null {
  if (style === null || style === undefined) return null;

  const classes = [
    style.foregroundArgb === null ? null : 'ecr-cell-styled',
    style.backgroundArgb === null ? null : 'ecr-cell-filled',
  ].filter((part): part is string => part !== null);

  return classes.length === 0 ? null : classes.join(' ');
}

/**
 * Поверхня сітки в світлій і темній темі — те, на чому стоїть текст комірки
 * без заливки.
 *
 * ⚠ Числа — ВИМІРЯНІ на живій сітці (`X-10`: `#242424` у темній), і це ті
 * самі значення, що дає тема: `--mantine-color-body` світлої — `#fff`,
 * темної — `dark[7]`.
 */
export const LightSurface = '#ffffff';
export const DarkSurface = '#242424';

/** Колір, якщо він читається на `surface` (`AA.text`); інакше `null`. */
function readableOn(color: string, surface: string): string | null {
  return contrast(color, surface) >= AA.text ? color : null;
}

/** Чорний чи білий — що дає більший контраст із заливкою. */
function bestTextOn(fill: string): string {
  return contrast('#000000', fill) >= contrast('#ffffff', fill) ? '#000000' : '#ffffff';
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

/** ARGB (`StyleDef.ForegroundArgb`, знак .NET `int`) → `#rrggbb`; альфа ігнорується. */
function hexOfArgb(argb: number): string {
  const rgb = argb & 0x00ffffff;

  return `#${rgb.toString(16).padStart(6, '0')}`;
}
