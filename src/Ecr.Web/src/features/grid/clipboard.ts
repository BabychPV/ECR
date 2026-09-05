/**
 * Обмін із буфером Excel (критерій FQ-1 №3 і №4).
 *
 * ⚠ Найчастіша причина, з якої grid-бібліотека не підходить. Тому розбір і
 * складання винесені у **чисті функції**: їх можна перевірити без DOM, без
 * бібліотеки і без сервера — і саме вони визначають, чи потраплять числа в
 * правильні комірки.
 */

/** Розібраний буфер: рядки × колонки. */
export type ClipboardMatrix = string[][];

/**
 * Роздільник колонок в Excel.
 *
 * ⛔ Саме табуляція, а не крапка з комою. Excel кладе в буфер `text/plain` із
 * табуляціями незалежно від регіональних налаштувань; крапка з комою — це
 * роздільник у CSV-файлі, і плутати їх означає розкласти один рядок у одну
 * колонку.
 */
const ColumnSeparator = '\t';

/**
 * Розбирає буфер обміну Excel.
 *
 * ⚠ Порожній хвостовий рядок відкидається: Excel завершує буфер переносом, і
 * без цього кожна вставка додавала б порожній рядок унизу — тихо і щоразу.
 */
export function parseClipboard(text: string): ClipboardMatrix {
  if (text.length === 0) return [];

  const rows = text.replace(/\r\n/g, '\n').replace(/\r/g, '\n').split('\n');

  while (rows.length > 0 && rows[rows.length - 1] === '') {
    rows.pop();
  }

  return rows.map((row) => row.split(ColumnSeparator));
}

/**
 * Складає буфер у форматі, який приймає Excel.
 *
 * ⚠ Завершальний перенос обов'язковий: без нього Excel вставляє останній
 * рядок у поточну комірку замість наступної — зсув на один рядок, який
 * помічають не одразу.
 */
export function toClipboard(matrix: ClipboardMatrix): string {
  return matrix.map((row) => row.join(ColumnSeparator)).join('\n') + '\n';
}

/**
 * Розбирає число з урахуванням локалі користувача.
 *
 * ⚠ Кома як десятковий роздільник — норма для uk/ru/kz, і Excel кладе в буфер
 * саме те, що показує. Прочитати «12,5» як текст означає, що колонка типу
 * `Decimal` мовчки отримає рядок і впаде на валідації вже після відправки —
 * тобто користувач побачить помилку там, де помилки не робив.
 *
 * ⛔ Але «1,234» не перетворюється на 1234: розділювач тисяч у буфері з Excel
 * приходить нерозривним пробілом або пробілом, і трактувати кому як групування
 * означало б перетворити «одна ціла двісті тридцять чотири» на «тисячу
 * двісті тридцять чотири» — правдоподібне число, помилку в якому знайдуть на
 * звірці.
 */
export function parseNumber(raw: string): number | null {
  const trimmed = raw.trim().replace(/[\s  ]/g, '');
  if (trimmed.length === 0) return null;

  const normalized = trimmed.includes(',') && !trimmed.includes('.')
    ? trimmed.replace(',', '.')
    : trimmed;

  if (!/^[+-]?\d+(\.\d+)?([eE][+-]?\d+)?$/.test(normalized)) return null;

  const value = Number(normalized);

  return Number.isFinite(value) ? value : null;
}

/** Комірка, у яку лягає вставлене значення. */
export interface PasteTarget {
  rowKey: string;
  columnCode: string;
  value: string;
}

/** Комірка, у яку вставити не можна, і причина. */
export interface PasteRejection {
  rowKey: string;
  columnCode: string;
  reason: string;
}

/** Результат розкладки буфера по сітці. */
export interface PastePlan {
  /** Що буде записано; порожньо, якщо є хоч одна заборонена комірка. */
  targets: PasteTarget[];
  /** Заборонені комірки з причинами — їх показують користувачеві. */
  rejected: PasteRejection[];
}

/** Чи можна писати в комірку і чому ні. */
export type CellGuard = (rowKey: string, columnCode: string) => string | null;

/**
 * Розкладає буфер по сітці від якірної комірки.
 *
 * ⛔ **Наявність хоч однієї забороненої комірки відхиляє ВЕСЬ батч.** Часткове
 * застосування заборонене на рівні API (B04 §2.3), і UI не має його імітувати:
 * інакше користувач бачив би, що «вставилося», і не помітив би, що половина
 * чисел не потрапила.
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

      const reason = guard(rowKey, columnCode);

      if (reason !== null) {
        rejected.push({ rowKey, columnCode, reason });
        continue;
      }

      targets.push({ rowKey, columnCode, value: row[c] ?? '' });
    }
  }

  return rejected.length > 0 ? { targets: [], rejected } : { targets, rejected };
}
