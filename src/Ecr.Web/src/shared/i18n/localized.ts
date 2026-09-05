import { DefaultLanguage, language } from './index';

/**
 * Локалізований текст так, як його віддає сервер.
 *
 * ⚠ Колонка `…L10n` — це **об'єкт**, а не рядок: одна колонка на всі мови,
 * бо «додати мову = запис у реєстр, а не колонка в двадцяти таблицях»
 * (ФВ-2.2). Тому в JSON приходить `{ values: { uk: "…", en: "…" } }`.
 */
export interface LocalizedText {
  values?: Record<string, string> | null;
}

/**
 * Текст мовою користувача.
 *
 * ⛔ Ланцюг запасних варіантів обов'язковий: мова → мова за замовчуванням →
 * будь-яка наявна → порожньо. До аудиту (`A7-05`) екрани клали такий об'єкт
 * прямо в JSX і показували `[object Object]` — тобто назва аркуша, таблиці й
 * довідника не читалася взагалі.
 */
export function localized(text: LocalizedText | null | undefined, lang = language()): string {
  const values = text?.values;
  if (values === null || values === undefined) return '';

  const exact = values[lang];
  if (exact !== undefined && exact.length > 0) return exact;

  const fallback = values[DefaultLanguage];
  if (fallback !== undefined && fallback.length > 0) return fallback;

  // Перше наявне краще за порожнечу: підпис, якого немає, виглядає як
  // зламаний інтерфейс, а не як невідкладений переклад.
  return Object.values(values).find((value) => value.length > 0) ?? '';
}
