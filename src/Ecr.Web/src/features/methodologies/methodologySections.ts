/**
 * Пам'ять «секцію згорнуто/розгорнуто» на сторінці версії методології.
 *
 * ⚠ Ключ — НА КОРИСТУВАЧА (`…sections.u<userId>`), а не один на браузер: на
 * спільній робочій станції методолог і рецензент бачать сторінку по-різному, і
 * вибір одного не має згортати секції іншому. Без відомого користувача —
 * спільний ключ, як `ecr.calcResults.collapsed`.
 *
 * ⚠ Сховище недоступне (приватне вікно, заблоковані дані сайту) — вибір просто
 * не запам'ятовується; сторінка працює за замовчуванням, а не падає.
 */
const Prefix = 'ecr.methodology.sections';

/** Ключ сховища для користувача. */
export function sectionsStorageKey(userId: number | null | undefined): string {
  return userId === null || userId === undefined ? Prefix : `${Prefix}.u${String(userId)}`;
}

/** Запам'ятований вибір: `sectionId → expanded`. Зіпсований запис — порожній вибір. */
export function readSectionChoices(userId: number | null | undefined): Record<string, boolean> {
  try {
    const raw = window.localStorage.getItem(sectionsStorageKey(userId));
    if (raw === null) return {};

    const parsed: unknown = JSON.parse(raw);
    if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) return {};

    const choices: Record<string, boolean> = {};
    for (const [key, value] of Object.entries(parsed as Record<string, unknown>)) {
      if (typeof value === 'boolean') choices[key] = value;
    }

    return choices;
  } catch {
    return {};
  }
}

/** Записує вибір однієї секції, не чіпаючи решти. */
export function writeSectionChoice(
  userId: number | null | undefined,
  sectionId: string,
  expanded: boolean,
): void {
  try {
    const next = { ...readSectionChoices(userId), [sectionId]: expanded };
    window.localStorage.setItem(sectionsStorageKey(userId), JSON.stringify(next));
  } catch {
    // Сховище недоступне — вибір просто не запам'ятається.
  }
}
