import { theme } from './theme';

/**
 * Щільність подання (`ФВ-14.14`, `D-131`).
 *
 * ⚠ Дві, а не «налаштовуваний масштаб»: три значення довелося б перевіряти в
 * кожній таблиці, а користь від третього нульова. `compact` за замовчуванням —
 * це відповідь на «скільки рядків я бачу без прокручування», а не економія.
 */
export type Density = 'compact' | 'comfortable';

const DensityKey = 'ecr.density';

/** Висота рядка таблиці для щільності, у пікселях. */
export function rowHeight(density: Density): number {
  const other = theme.other as { rowHeightCompact: number; rowHeightComfortable: number };

  return density === 'compact' ? other.rowHeightCompact : other.rowHeightComfortable;
}

/**
 * Обрана щільність.
 *
 * ⚠ Читання не падає ніколи: у приватному вікні і при заблокованих даних сайту
 * звернення до `localStorage` кидає виняток, і застосунок не піднявся б через
 * налаштування вигляду.
 */
export function density(): Density {
  try {
    return globalThis.localStorage?.getItem(DensityKey) === 'comfortable'
      ? 'comfortable'
      : 'compact';
  } catch {
    return 'compact';
  }
}

/** Запам'ятовує вибір щільності. */
export function setDensity(value: Density): void {
  try {
    globalThis.localStorage?.setItem(DensityKey, value);
  } catch {
    // Налаштування вигляду — не привід ламати роботу.
  }
}

/**
 * Застосовує щільність до документа.
 *
 * ⛔ Через CSS-змінну, а не через перерендер кожної таблиці. Щільність зачіпає
 * геть усі подання; пропустити її в одному з п'ятнадцяти означало б, що екран
 * «майже» перемкнувся — і це помітно гірше, ніж якби не перемкнувся зовсім.
 */
export function applyDensity(value: Density): void {
  document.documentElement.style.setProperty('--ecr-row-height', `${rowHeight(value)}px`);
  document.documentElement.dataset['ecrDensity'] = value;
}
