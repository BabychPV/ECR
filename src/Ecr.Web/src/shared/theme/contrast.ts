/**
 * Контраст за WCAG 2.1 (`ФВ-14.17`).
 *
 * ⚠ Обчислення, а не око. Око не відрізняє 2.9:1 від 3.1:1, а різниця між ними
 * — це різниця між «межу видно на поганому моніторі» і «межі немає». Первісна
 * жовта межа «брудної» комірки давала проти білого **1.8:1**, і жоден перегляд
 * екрана цього не показав би.
 */

/** Розбирає `#rgb` або `#rrggbb` у три складові 0…1. */
function channels(hex: string): [number, number, number] {
  const clean = hex.replace('#', '');

  const full =
    clean.length === 3
      ? clean
          .split('')
          .map((c) => c + c)
          .join('')
      : clean;

  return [0, 2, 4].map((i) => parseInt(full.slice(i, i + 2), 16) / 255) as [
    number,
    number,
    number,
  ];
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

/** Відношення контрасту двох кольорів: від 1 (однакові) до 21 (чорний/білий). */
export function contrast(a: string, b: string): number {
  const first = luminance(a);
  const second = luminance(b);

  const lighter = Math.max(first, second);
  const darker = Math.min(first, second);

  return (lighter + 0.05) / (darker + 0.05);
}

/** Пороги AA: текст і елементи керування (`ФВ-14.17`). */
export const AA = { text: 4.5, nonText: 3 } as const;
