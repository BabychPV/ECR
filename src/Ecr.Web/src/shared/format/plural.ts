import { t, type Language } from '@/shared/i18n';
import { formatLocale } from './locale';
import { formatNumber } from './number';

/**
 * Множина — `Intl.PluralRules` із ключами `.one/.few/.many/.other` (`D15-08`).
 *
 * ⛔ Англійська логіка «одне або решта» для `ru` НЕПРАВИЛЬНА, і не «трохи»:
 * 1 рядок, 2 рядкИ, 5 рядкІВ, 21 рядОК. Причому 21 повертається до форми
 * одиниці — тобто навіть правило «1 — окремо, решта — разом» ламається на
 * другому десятку. Заміряно на V8 (Node 24.19), `Intl.PluralRules().select()`:
 *
 *   ru: 1→one  2→few   5→many  21→one   0→many
 *   kk: 1→one  2→other 5→other 21→other 0→other
 *   en: 1→one  2→other 5→other 21→other 0→other
 *
 * ⚠ З цієї ж таблиці видно, що `kk` за категоріями збігається з `en` — форм у
 * казахській дві, а не чотири. Тому доказ «локаль не переплутано» для
 * казахської будується НЕ на множині (вона не відрізнила б `kk` від `en`), а
 * на числах і датах, де розбіжність величезна. Множина ж відрізняє `ru` від
 * обох — і саме на 2, 5, 21.
 */

/** Категорія множини для кількості. */
export type PluralCategory = Intl.LDMLPluralRule;

const rules = new Map<string, Intl.PluralRules>();

function pluralRules(locale: string): Intl.PluralRules {
  const hit = rules.get(locale);
  if (hit !== undefined) return hit;

  const made = new Intl.PluralRules(locale, { type: 'cardinal' });
  rules.set(locale, made);

  return made;
}

/**
 * Категорія множини для кількості мовою інтерфейсу.
 *
 * ⚠ Нескінченність і `NaN` дають `other`: `Intl.PluralRules.select()` на них
 * не визначений як «правильна» відповідь, а `other` — єдина категорія, яка
 * існує в КОЖНІЙ мові, тобто єдина, під яку ключ у каталозі точно є.
 */
export function pluralCategory(count: number, lang?: Language): PluralCategory {
  if (!Number.isFinite(count)) return 'other';

  return pluralRules(formatLocale(lang)).select(count);
}

/**
 * Рядок каталогу, узгоджений із кількістю: `<база>.<категорія>`.
 *
 * ⚠ Параметр `{count}` підставляється ВЖЕ відформатованим (`formatNumber`), а
 * не `String(count)`: інакше в російському реченні з правильною формою слова
 * стояло б `1234` без роздільника тисяч — тобто напис був би наполовину
 * локалізований. Місце виклику може перекрити його через `params`.
 *
 * ⛔ Запасного переходу на `.other` тут НЕМАЄ, і це рішення, а не пропуск.
 * Відсутній `rows.few` означає, що каталог для цієї мови неповний, і показати
 * замість нього `rows.other` — значить приховати прогалину: російський
 * інтерфейс тихо писав би «2 рядків». `t()` уже має правильну відповідь на
 * брак ключа — видима позначка `⟦rows.few⟧` і скарга в консоль розробника
 * (`D-138`), — і саме вона тут і потрібна.
 */
export function formatCount(
  count: number,
  keyBase: string,
  params?: Record<string, string | number>,
  lang?: Language,
): string {
  const category = pluralCategory(count, lang);

  return t(`${keyBase}.${category}`, { count: formatNumber(count, undefined, lang), ...params });
}
