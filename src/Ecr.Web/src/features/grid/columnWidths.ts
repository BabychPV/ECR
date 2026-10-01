/**
 * Ширини колонок зберігаються **для користувача і таблиці** (`ФВ-14.29`).
 *
 * ⚠ Оператор відкриває ту саму таблицю щоранку і щоразу підганяє під себе три
 * колонки: назву ширшою, службові вужчими. Ширини, які не переживають
 * перезавантаження, — це та сама робота двісті разів на рік.
 *
 * ⛔ `D-201`: ширини — на КОРИСТУВАЧА і ВИЗНАЧЕННЯ таблиці (`tableDefId`), на
 * сервері (`BE-20`, ключ `grid.columnWidths.{tableDefId}`); `localStorage` —
 * лише кеш першого рендера. Ключ — визначення, а не екземпляр: екземпляр
 * новий на кожен період, і ширини губилися б щомісяця. Компроміс названо в
 * `D-201`: ширини однакові на всіх робочих місцях користувача.
 *
 * Збереження — `features/preferences/columnWidthsSync.ts` (`useColumnWidths`);
 * тут лишилися типова ширина і розбір події RevoGrid.
 */

/** Ширина за замовчуванням, якщо збереженої немає. */
export const DefaultColumnWidth = 140;

/**
 * Початкова ширина колонки: користувацька (D-201) → шаблонна `WidthPx`
 * (D-234) → типова. Користувацька лише перекриває шаблонну, не змінює її.
 */
export function columnWidth(
  userWidth: number | undefined,
  templateWidth: number | null | undefined,
): number {
  return userWidth ?? templateWidth ?? DefaultColumnWidth;
}

/**
 * Розбирає подію зміни ширини RevoGrid.
 *
 * ⛔ Розбір винесено в чисту функцію навмисно. Подія типізована як
 * `unknown`-словник, і перевірити її на змонтованому веб-компоненті в jsdom
 * дорого — а помилка тут мовчазна: ширини просто не зберігаються, і ніхто
 * ніколи не дізнається, чому.
 */
export function widthsFromEvent(detail: unknown): Record<string, number> {
  if (typeof detail !== 'object' || detail === null) return {};

  const widths: Record<string, number> = {};

  // RevoGrid віддає словник «індекс колонки → опис колонки».
  for (const column of Object.values(detail as Record<string, unknown>)) {
    if (typeof column !== 'object' || column === null) continue;

    const { prop, size } = column as { prop?: unknown; size?: unknown };

    if (typeof prop === 'string' && typeof size === 'number' && size > 0) {
      widths[prop] = size;
    }
  }

  return widths;
}
