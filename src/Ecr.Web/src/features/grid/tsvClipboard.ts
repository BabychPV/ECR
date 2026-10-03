/**
 * Розбір і складання TSV-буфера Excel (RFC-4180 для комірок з переносом, табом, лапкою).
 *
 * ⚠ Окремий модуль від `clipboard.ts` навмисно (бюджет маршруту D-132): `clipboard.ts` тягне
 * `edits.ts` -> `autosave.ts`, тобто статично входить у граф `DocumentPage`, а TSV потрібен лише
 * сітці й конструктору довідників.
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

  const normalized = text.replace(/\r\n/g, '\n').replace(/\r/g, '\n');
  // AN-39/L8-06: без лапок розбір простий (швидкий шлях, як було).
  const rows: string[][] = normalized.includes('"')
    ? parseQuoted(normalized)
    : normalized.split('\n').map((row) => row.split(ColumnSeparator));

  while (rows.length > 0) {
    const last = rows[rows.length - 1];
    if (last?.length === 1 && last[0] === '') rows.pop();
    else break;
  }

  return rows;
}

/**
 * Розбір TSV за RFC-4180 (так Excel кладе комірки з переносом, табом чи лапкою).
 *
 * ⛔ Без цього `"Boiler\nNo.2"<TAB>10` розпадався на ТРИ рядки: усе нижче зсувалося
 * й писалося в чужі комірки. Лапка відкриває поле лише на ПОЧАТКУ поля; `""`
 * всередині - одна лапка. Лапка посеред поля (`5" труба`) - звичайний символ.
 * Незакрите або не до кінця поля - читається буквально (як до зміни).
 */
function parseQuoted(text: string): string[][] {
  const rows: string[][] = [];
  const n = text.length;
  let row: string[] = [];
  let i = 0;

  for (;;) {
    let field: string | null = null;

    if (text[i] === '"') {
      let j = i + 1;
      let buf = '';
      let closed = false;

      while (j < n) {
        if (text[j] === '"') {
          if (text[j + 1] === '"') {
            buf += '"';
            j += 2;
            continue;
          }

          closed = true;
          break;
        }

        buf += text[j];
        j++;
      }

      const next = text[j + 1];
      if (closed && (next === undefined || next === '\t' || next === '\n')) {
        field = buf;
        i = j + 1;
      }
    }

    if (field === null) {
      let end = i;
      while (end < n && text[end] !== '\t' && text[end] !== '\n') end++;
      field = text.slice(i, end);
      i = end;
    }

    row.push(field);

    if (i >= n) {
      rows.push(row);
      break;
    }

    if (text[i] === '\n') {
      rows.push(row);
      row = [];
    }

    i++;
    if (i >= n && text[i - 1] === '\n') break;
  }

  return rows;
}

/** Поле, яке Excel бере в лапки: таб, перенос або лапка всередині. */
function quoteField(field: string): string {
  return /[\t\n\r"]/.test(field) ? `"${field.replace(/"/g, '""')}"` : field;
}

/**
 * Складає буфер у форматі, який приймає Excel.
 *
 * ⚠ Завершальний перенос обов'язковий: без нього Excel вставляє останній
 * рядок у поточну комірку замість наступної — зсув на один рядок, який
 * помічають не одразу.
 */
export function toClipboard(matrix: ClipboardMatrix): string {
  return matrix.map((row) => row.map(quoteField).join(ColumnSeparator)).join('\n') + '\n';
}
