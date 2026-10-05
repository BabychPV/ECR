/**
 * Обмін із буфером Excel (критерій FQ-1 №3 і №4).
 *
 * ⚠ Найчастіша причина, з якої grid-бібліотека не підходить. Тому розбір і
 * складання винесені у **чисті функції**: їх можна перевірити без DOM, без
 * бібліотеки і без сервера — і саме вони визначають, чи потраплять числа в
 * правильні комірки.
 */

import { formatLocale } from '@/shared/format';
import { t } from '@/shared/i18n';
import type { ClipboardMatrix } from './tsvClipboard';

/**
 * Як прочитано текст буфера як число.
 *
 * - `number` — однозначне число; `text` — інваріантний запис (крапка як
 *   десятковий роздільник, без розрядів; експонента, якщо була, збережена).
 * - `ambiguous` — рядок читається як число ДВОМА способами, і локаль
 *   інтерфейсу не знімає неоднозначності (`1,234` в `en`). Вгадувати не можна.
 * - `text` — не число взагалі.
 */
type NumberReading =
  | { kind: 'number'; text: string }
  | { kind: 'ambiguous'; asGroup: string; asDecimal: string }
  | { kind: 'text' };

/** Роздільники локалі: розрядів і десятковий. */
interface Separators {
  group: string;
  decimal: string;
}

/**
 * Пам'ять роздільників за локаллю — `readNumber` кличеться на кожну комірку
 * буфера (500×60 у ФВ-14.4), а `formatToParts` щоразу читає дані ICU.
 */
const separatorsByLocale = new Map<string, Separators>();

/**
 * Роздільники локалі — з того самого `Intl.NumberFormat`, яким сітка ПОКАЗУЄ
 * числа (`formatDecimal` → `formatLocale()`). Одне джерело правди: що сітка
 * пише, те вставка й читає.
 */
function separatorsOf(locale: string): Separators {
  const hit = separatorsByLocale.get(locale);
  if (hit !== undefined) return hit;

  const parts = new Intl.NumberFormat(locale, { useGrouping: true }).formatToParts(1234567.5);
  const made: Separators = {
    group: parts.find((part) => part.type === 'group')?.value ?? '',
    decimal: parts.find((part) => part.type === 'decimal')?.value ?? '.',
  };

  separatorsByLocale.set(locale, made);

  return made;
}

/** Чи є рядок коректним записом цілого з розрядами `separator` (`1,234,567`). */
function isGrouped(text: string, separator: string): boolean {
  const [head, ...rest] = text.split(separator);

  return (
    head !== undefined &&
    /^\d{1,3}$/.test(head) &&
    rest.length > 0 &&
    rest.every((group) => /^\d{3}$/.test(group))
  );
}

/**
 * Читає текст буфера як число за роздільниками ПОТОЧНОЇ локалі інтерфейсу.
 *
 * ⛔ До 2026-09-28 «єдина кома» завжди вважалася десятковою. Але англійський
 * інтерфейс сам показує `4,242` (кома — розряди), і значення з en-US Excel
 * `1,234` лягало в `Decimal` як `1.234` — у тисячу разів менше, і сервер це
 * приймав. Тепер правила такі (пробіли, зокрема нерозривні, — завжди розряди
 * і відкидаються):
 *
 * 1. Є і крапка, і кома — десятковий той, що стоїть ОСТАННІМ і лише один раз;
 *    другий мусить бути коректними розрядами (`1,234.5`, `1.234,5`).
 * 2. Один роздільник кілька разів — лише розряди (`1,234,567`).
 * 3. Один роздільник один раз, і він НЕ може бути розрядом (після нього не
 *    рівно три цифри, або перед ним понад три цифри чи ведучий нуль) —
 *    десятковий (`12,5`, `1234,5`, `0,125`).
 * 4. Один роздільник один раз між `1–3 цифрами` і `3 цифрами` — структурно
 *    можливі обидва прочитання. Вирішує локаль: це її десятковий роздільник —
 *    десятковий (`1,234` в `uk` → 1.234); це її роздільник розрядів —
 *    **неоднозначно**, відмова (`1,234` в `en`). Крапка, яка в локалі не є
 *    розрядами, — інваріантний запис (так копіює сама сітка, `cellText`), тобто
 *    десяткова; кома, яка в локалі ні те ні се, — неоднозначна.
 *
 * ⚠ Локаль за замовчуванням — `formatLocale()`: та сама, якою форматує сітка.
 */
export function readNumber(raw: string, locale: string = formatLocale()): NumberReading {
  // ⛔ T2-10: пробіл усередині — лише розряди тисяч (`1 234 567`); `1 2` мовчки ставав 12 (як і на сервері).
  const trimmed = raw.trim();
  if (/\s/.test(trimmed) && !/^[+-]?\d{1,3}(?:\s\d{3})+(?:[.,]\d+)?(?:[eE][+-]?\d+)?$/.test(trimmed)) {
    return { kind: 'text' };
  }

  const stripped = trimmed.replace(/\s/g, '');
  if (stripped.length === 0) return { kind: 'text' };

  const match =/^([+-]?)([\d.,]+)(?:[eE]([+-]?\d+))?$/.exec(stripped);
  if (match === null) return { kind: 'text' };

  const sign = match[1] ?? '';
  const mantissa = match[2] ?? '';
  const exponent = match[3] === undefined ? '' : `e${match[3]}`;

  // `.5` / `,5` — число (0.5), як і на сервері, але лише з десятковим роздільником локалі (крапка без
  // розрядів — інваріантний запис); `1.`, `1,` — не числа.
  const lead = /^([.,])(\d+)$/.exec(mantissa);
  if (lead !== null) {
    const { group: leadGroup, decimal: leadDecimal } = separatorsOf(locale);
    const separator = lead[1];
    const ok = separator === leadDecimal || (separator === '.' && leadGroup !== '.');

    return ok ? { kind: 'number', text: `${sign}0.${lead[2] ?? ''}${exponent}` } : { kind: 'text' };
  }

  if (!/^\d/.test(mantissa) || !/\d$/.test(mantissa)) return { kind: 'text' };

  const number = (int: string, frac: string): NumberReading => ({
    kind: 'number',
    text: `${sign}${int}${frac.length > 0 ? `.${frac}` : ''}${exponent}`,
  });

  const commas = mantissa.split(',').length - 1;
  const dots = mantissa.split('.').length - 1;

  if (commas === 0 && dots === 0) return number(mantissa, '');

  if (commas > 0 && dots > 0) {
    const last = Math.max(mantissa.lastIndexOf(','), mantissa.lastIndexOf('.'));
    const decimal = mantissa.charAt(last);
    const group = decimal === ',' ? '.' : ',';
    if ((decimal === ',' ? commas : dots) !== 1) return { kind: 'text' };

    const int = mantissa.slice(0, last);
    if (!isGrouped(int, group)) return { kind: 'text' };

    return number(int.split(group).join(''), mantissa.slice(last + 1));
  }

  const separator = commas > 0 ? ',' : '.';

  if (commas + dots > 1) {
    return isGrouped(mantissa, separator)
      ? number(mantissa.split(separator).join(''), '')
      : { kind: 'text' };
  }

  const at = mantissa.indexOf(separator);
  const before = mantissa.slice(0, at);
  const after = mantissa.slice(at + 1);

  const canBeGroup = after.length === 3 && /^[1-9]\d{0,2}$/.test(before);
  if (!canBeGroup) return number(before, after);

  const { group, decimal } = separatorsOf(locale);

  if (separator === decimal) return number(before, after);
  if (separator === '.' && group !== '.') return number(before, after);

  return {
    kind: 'ambiguous',
    asGroup: `${sign}${before}${after}${exponent}`,
    asDecimal: `${sign}${before}.${after}${exponent}`,
  };
}

/**
 * Розбирає число з урахуванням локалі інтерфейсу (правила — `readNumber`).
 *
 * ⚠ Кома як десятковий роздільник — норма для uk/ru/kz, і Excel кладе в буфер
 * саме те, що показує. Прочитати «12,5» як текст означає, що колонка типу
 * `Decimal` мовчки отримає рядок і впаде на валідації вже після відправки.
 *
 * ⛔ Неоднозначне (`1,234` в `en`) — `null`, а не число: вгадане прочитання
 * тихо змінює значення в тисячу разів. Вставка таке відхиляє з поясненням
 * (`planPaste`).
 */
export function parseNumber(raw: string, locale: string = formatLocale()): number | null {
  const reading = readNumber(raw, locale);
  if (reading.kind !== 'number') return null;

  const value = Number(reading.text);

  return Number.isFinite(value) ? value : null;
}

/** Комірка, у яку лягає вставлене значення. */
interface PasteTarget {
  rowKey: string;
  columnCode: string;
  value: string;
}

/** Комірка, у яку вставити не можна, і причина. */
export interface PasteRejection {
  rowKey: string;
  columnCode: string;
  reason: string;
  /**
   * Чому відхилено: `guard` — права/«рахує система»/закрита комірка;
   * `ambiguous` — число читається двома способами (T5-04: вступ вікна має
   * називати саме цю причину, а не «лише для читання»).
   */
  kind: 'guard' | 'ambiguous';
}

/** Результат розкладки буфера по сітці. */
interface PastePlan {
  /** Що буде записано; порожньо, якщо є хоч одна заборонена комірка. */
  targets: PasteTarget[];
  /** Заборонені комірки з причинами — їх показують користувачеві. */
  rejected: PasteRejection[];
}

/** Чи можна писати в комірку і чому ні. */
type CellGuard = (rowKey: string, columnCode: string) => string | null;

/**
 * Причина відмови для неоднозначного числа; `null` — значення однозначне.
 *
 * ⛔ Неоднозначне НЕ вгадується: `1,234` в англійському інтерфейсі — це і
 * «тисяча двісті тридцять чотири», і «одна ціла двісті тридцять чотири
 * тисячних», і будь-який вибір тихо псує дані в тисячу разів. Людина бачить
 * обидва прочитання і вставляє однозначний запис.
 */
function ambiguityOf(value: string, locale: string): string | null {
  // ⚠ Без коми неоднозначності не буває (крапка неоднозначна лише там, де вона
  // розряди, — тому перевіряються обидві). Швидкий вихід тримає ФВ-14.4
  // (500×60 за < 200 мс): у повному наборі під навантаженням повний розбір
  // кожної комірки дав 492 мс.
  if (!value.includes(',') && !value.includes('.')) return null;

  const reading = readNumber(value, locale);
  if (reading.kind !== 'ambiguous') return null;

  return t('grid.pasteAmbiguousNumber', {
    value: value.trim(),
    asGroup: reading.asGroup,
    asDecimal: reading.asDecimal,
  });
}

/**
 * Розкладає буфер по сітці від якірної комірки.
 *
 * ⛔ **Наявність хоч однієї забороненої комірки відхиляє ВЕСЬ батч.** Часткове
 * застосування заборонене на рівні API (B04 §2.3), і UI не має його імітувати:
 * інакше користувач бачив би, що «вставилося», і не помітив би, що половина
 * чисел не потрапила.
 *
 * ⛔ Заборонена комірка — це й комірка з НЕОДНОЗНАЧНИМ числом (`readNumber`):
 * вона відхиляється тим самим шляхом, із поясненням, а не вставляється
 * вгаданою.
 *
 * ⚠ `isNumericColumn` звужує перевірку неоднозначності до числових колонок.
 * Без нього перевіряються ВСІ колонки: безпечніше відхилити `1,234` у
 * текстовій колонці, ніж тихо записати `1.234` у десяткову.
 *
 * ⚠ Буфер, більший за сітку, обрізається мовчки — це нормальна поведінка
 * Excel: вставка в кут таблиці не має створювати рядків, яких у документі
 * немає.
 */
export function planPaste(
  matrix: ClipboardMatrix,
  rowKeys: readonly string[],
  columnCodes: readonly string[],
  anchor: { rowIndex: number; columnIndex: number },
  guard: CellGuard,
  isNumericColumn: (columnCode: string) => boolean = () => true,
  locale: string = formatLocale(),
): PastePlan {
  const targets: PasteTarget[] = [];
  const rejected: PasteRejection[] = [];

  for (let r = 0; r < matrix.length; r++) {
    const rowKey = rowKeys[anchor.rowIndex + r];
    if (rowKey === undefined) break;

    const row = matrix[r] ?? [];

    for (let c = 0; c < row.length; c++) {
      const columnCode = columnCodes[anchor.columnIndex + c];
      if (columnCode === undefined) break;

      const value = row[c] ?? '';
      const guarded = guard(rowKey, columnCode);
      const ambiguous =
        guarded === null && isNumericColumn(columnCode) ? ambiguityOf(value, locale) : null;

      if (guarded !== null) {
        rejected.push({ rowKey, columnCode, reason: guarded, kind: 'guard' });
        continue;
      }

      if (ambiguous !== null) {
        rejected.push({ rowKey, columnCode, reason: ambiguous, kind: 'ambiguous' });
        continue;
      }

      targets.push({ rowKey, columnCode, value });
    }
  }

  return rejected.length > 0 ? { targets: [], rejected } : { targets, rejected };
}
