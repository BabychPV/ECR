/**
 * Контраст за WCAG 2.1 (`ФВ-14.17`).
 *
 * ⚠ Обчислення, а не око. Око не відрізняє 2.9:1 від 3.1:1, а різниця між ними
 * — це різниця між «межу видно на поганому моніторі» і «межі немає». Первісна
 * жовта межа «брудної» комірки давала проти білого **1.8:1**, і жоден перегляд
 * екрана цього не показав би.
 */

/** Колір як чотири складові: `r`/`g`/`b` у 0…255, `a` у 0…1. */
export interface Rgba {
  readonly r: number;
  readonly g: number;
  readonly b: number;
  readonly a: number;
}

/**
 * Розбирає `#rgb`, `#rrggbb`, `rgb(...)` або `rgba(...)`.
 *
 * ⚠ `rgba()` тут не з примхи. Первісно розбирався лише hex — і саме тому
 * перевірка контрасту сітки була неможлива: RevoGrid задає колір тексту
 * комірки як `rgba(0, 0, 0, 0.87)` (`revo-grid-style.css`,
 * `revo-grid[theme=compact] revogr-data .rgCell`). Токен, який неможливо
 * розібрати, не можна й перевірити — і в темній темі текст, введений
 * оператором, давав 1.35:1, тобто був нечитний, а жоден тест цього не бачив.
 */
export function parseColor(value: string): Rgba {
  const text = value.trim();

  const fn = /^rgba?\(\s*([\d.]+)[\s,]+([\d.]+)[\s,]+([\d.]+)(?:[\s,/]+([\d.]+%?))?\s*\)$/i.exec(text);
  if (fn !== null) {
    const alpha = fn[4] ?? '1';

    return {
      r: Number(fn[1]),
      g: Number(fn[2]),
      b: Number(fn[3]),
      a: alpha.endsWith('%') ? Number(alpha.slice(0, -1)) / 100 : Number(alpha),
    };
  }

  const clean = text.replace('#', '');
  if (!/^[0-9a-f]{3,8}$/i.test(clean)) throw new Error(`не колір: ${value}`);

  const full =
    clean.length === 3 || clean.length === 4
      ? clean
          .split('')
          .map((c) => c + c)
          .join('')
      : clean;

  const byte = (i: number): number => parseInt(full.slice(i, i + 2), 16);

  return { r: byte(0), g: byte(2), b: byte(4), a: full.length === 8 ? byte(6) / 255 : 1 };
}

/**
 * Накладає напівпрозорий колір на непрозорий фон і повертає `#rrggbb`.
 *
 * ⛔ Без цього кроку контраст напівпрозорого тексту рахувався б так, ніби
 * альфи немає: `rgba(0,0,0,0.87)` дало б проти `#242424` 21:1 замість
 * справжніх 1.35:1 — тобто перевірка показувала б рівно протилежне тому, що
 * бачить оператор.
 */
export function flatten(color: string, background: string): string {
  const fg = parseColor(color);
  if (fg.a >= 1) return hexOf(fg);

  const bg = parseColor(background);
  const mix = (f: number, b: number): number => Math.round(f * fg.a + b * (1 - fg.a));

  return hexOf({ r: mix(fg.r, bg.r), g: mix(fg.g, bg.g), b: mix(fg.b, bg.b), a: 1 });
}

/** `#rrggbb` із трьох складових. */
function hexOf(color: Rgba): string {
  const part = (v: number): string => Math.round(v).toString(16).padStart(2, '0');

  return `#${part(color.r)}${part(color.g)}${part(color.b)}`;
}

/** Три складові 0…1 — вхід для гамма-корекції. */
function channels(value: string): [number, number, number] {
  const { r, g, b } = parseColor(value);

  return [r / 255, g / 255, b / 255];
}

/**
 * Відносна яскравість.
 *
 * ⚠ Гамма-корекція обов'язкова: середнє арифметичне складових дало б для
 * насиченого синього ту саму яскравість, що й для сірого 50 %, і всі
 * перевірки контрасту стали б вигадкою.
 */
export function luminance(hex: string): number {
  const [r, g, b] = channels(hex).map((c) =>
    c <= 0.04045 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4,
  ) as [number, number, number];

  return 0.2126 * r + 0.7152 * g + 0.0722 * b;
}

/**
 * Відношення контрасту двох кольорів: від 1 (однакові) до 21 (чорний/білий).
 *
 * ⛔ Напівпрозорий колір відхиляється, а не приймається мовчки. WCAG рахує
 * контраст того, що видно на екрані, — тобто вже накладеного кольору; мовчки
 * відкинута альфа дала б для `rgba(0,0,0,0.87)` 21:1 замість 1.35:1. Виклик
 * зобов'язаний спершу пройти через `flatten()` і тим САМЕ назвати фон, поверх
 * якого колір лежить.
 */
export function contrast(a: string, b: string): number {
  for (const value of [a, b]) {
    if (parseColor(value).a < 1) {
      throw new Error(`контраст напівпрозорого кольору не визначений: ${value} — спершу flatten()`);
    }
  }

  const first = luminance(a);
  const second = luminance(b);

  const lighter = Math.max(first, second);
  const darker = Math.min(first, second);

  return (lighter + 0.05) / (darker + 0.05);
}

/** Пороги AA: текст і елементи керування (`ФВ-14.17`). */
export const AA = { text: 4.5, nonText: 3 } as const;
