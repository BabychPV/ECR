/**
 * Ширини колонок зберігаються **для користувача і таблиці** (`ФВ-14.29`).
 *
 * ⚠ Оператор відкриває ту саму таблицю щоранку і щоразу підганяє під себе три
 * колонки: назву ширшою, службові вужчими. Ширини, які не переживають
 * перезавантаження, — це та сама робота двісті разів на рік.
 *
 * ⛔ У `localStorage`, а не на сервері, і це свідомо: це налаштування ОДНОГО
 * робочого місця, а не профілю. Той самий оператор на іншому моніторі хоче
 * інші ширини, і синхронізувати їх між машинами означало б псувати обидві.
 */

/** Ширина за замовчуванням, якщо збереженої немає. */
export const DefaultColumnWidth = 140;

/** Ключ сховища: таблиця плюс період не потрібен — розмітка та сама. */
function storageKey(tableInstanceId: number): string {
  return `ecr.columnWidths:${tableInstanceId}`;
}

/**
 * Читає збережені ширини: код колонки → пікселі.
 *
 * ⚠ Ніколи не падає і ніколи не повертає сміття: пошкоджений або чужий вміст
 * трактується як «збереженого немає». Налаштування вигляду не має права
 * ламати таблицю, заради якої існує система.
 */
export function readWidths(tableInstanceId: number): Record<string, number> {
  try {
    const raw = globalThis.localStorage?.getItem(storageKey(tableInstanceId));
    if (raw === null || raw === undefined) return {};

    const parsed: unknown = JSON.parse(raw);
    if (typeof parsed !== 'object' || parsed === null) return {};

    const widths: Record<string, number> = {};
    for (const [code, value] of Object.entries(parsed as Record<string, unknown>)) {
      if (typeof value === 'number' && Number.isFinite(value) && value > 0) {
        widths[code] = value;
      }
    }

    return widths;
  } catch {
    return {};
  }
}

/** Дописує ширини, не стираючи решти. */
export function saveWidths(tableInstanceId: number, changed: Record<string, number>): void {
  try {
    const merged = { ...readWidths(tableInstanceId), ...changed };

    globalThis.localStorage?.setItem(storageKey(tableInstanceId), JSON.stringify(merged));
  } catch {
    // Приватне вікно і заблоковані дані сайту: ширини не збережуться, і це
    // все, що станеться.
  }
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
