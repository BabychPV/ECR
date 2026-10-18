import { parseBlock, pastedBool } from '@/features/registries/rc812/rowModel';
import type { PermitCellValue, PermitColumn } from './columns';

/**
 * Вставка блоку з Excel у таблицю перміта (PM-06). Мокап: діалог «Paste from Excel…»
 * (`screens-permits.js`, `ctx.dialog('paste-rows')`). Один рядок тексту = один рядок таблиці,
 * стовпці — у порядку {@link PasteColumn}. Розбір на рядки/клітинки й «так/ні» — із
 * `features/registries/rc812/rowModel.ts` (`parseBlock`, `pastedBool`), не копія.
 *
 * ⚠ Мокап брав `parseFloat` (він приймає «12abc» як 12). Тут число розбирається суворо, а
 * нерозпізнане не вигадується, а стає `null` із записом у `issues` — рядок усе одно додається і
 * позначається в таблиці, так що людина виправляє його там (текст підказки діалогу).
 */

/** Стовпець для вставки: порядок масиву = порядок клітинок. */
export type PasteColumn = Pick<PermitColumn, 'code' | 'type'>;

export type PasteIssueReason = 'number' | 'bool' | 'date';

/** Нерозпізнана клітинка: номер рядка вставки (з 1, без порожніх), стовпець і сирий текст. */
export interface PasteIssue {
  readonly row: number;
  readonly column: string;
  readonly text: string;
  readonly reason: PasteIssueReason;
}

export interface PastedRow {
  readonly values: Readonly<Record<string, PermitCellValue>>;
}

export interface PasteResult {
  readonly rows: PastedRow[];
  readonly issues: PasteIssue[];
  /** Клітинок правіше за останній стовпець (відкинуто). */
  readonly extraCells: number;
}

const SpaceLike = /[\s   ]/g;
const PlainNumber = /^[+-]?(?:\d+\.?\d*|\.\d+)(?:e[+-]?\d+)?$/i;

/** `1,234,567` / `1.234.567` → `1234567`; будь-яка інша форма з кількома роздільниками — не число. */
function thousandsOnly(text: string, separator: ',' | '.'): string {
  const escaped = separator === '.' ? '\\.' : ',';
  const grouped = new RegExp(`^[+-]?\\d{1,3}(?:${escaped}\\d{3})+$`);
  return grouped.test(text) ? text.split(separator).join('') : 'NaN';
}

/**
 * Число з Excel: пробіли (в т.ч. нерозривні) як роздільник тисяч; десятковий роздільник — кома або
 * крапка. Якщо є обидва знаки, десятковим є ОСТАННІЙ, інший — тисячі (`1.234,5` і `1,234.5` →
 * 1234.5). Одна кома без крапки — десяткова (`1,234` → 1.234): формат російськомовного Excel.
 * `null` — не число.
 */
export function parseLocaleNumber(text: string): number | null {
  const compact = text.replace(SpaceLike, '');
  if (compact === '') return null;

  const lastComma = compact.lastIndexOf(',');
  const lastDot = compact.lastIndexOf('.');
  let normalized: string;

  if (lastComma >= 0 && lastDot >= 0) {
    const decimal = lastComma > lastDot ? ',' : '.';
    const thousands = decimal === ',' ? /\./g : /,/g;
    normalized = compact.replace(thousands, '').replace(decimal, '.');
  } else if (lastComma >= 0) {
    // Кілька ком — лише групи тисяч (`1,234,567`); одна кома — десяткова.
    normalized = compact.split(',').length > 2 ? thousandsOnly(compact, ',') : compact.replace(',', '.');
  } else {
    normalized = compact.split('.').length > 2 ? thousandsOnly(compact, '.') : compact;
  }

  if (!PlainNumber.test(normalized)) return null;
  const value = Number(normalized);
  return Number.isFinite(value) ? value : null;
}

/** Дата: ISO `РРРР-ММ-ДД` або `Д.М.РРРР` (формат Excel); справжня календарна. Інше — `null`. */
export function parseLocaleDate(text: string): string | null {
  const value = text.trim();
  let year: number;
  let month: number;
  let day: number;

  const iso = /^(\d{4})-(\d{1,2})-(\d{1,2})$/.exec(value);
  const dotted = /^(\d{1,2})\.(\d{1,2})\.(\d{4})$/.exec(value);
  if (iso !== null) {
    year = Number(iso[1]);
    month = Number(iso[2]);
    day = Number(iso[3]);
  } else if (dotted !== null) {
    day = Number(dotted[1]);
    month = Number(dotted[2]);
    year = Number(dotted[3]);
  } else {
    return null;
  }

  const probe = new Date(Date.UTC(year, month - 1, day));
  const real =
    probe.getUTCFullYear() === year && probe.getUTCMonth() === month - 1 && probe.getUTCDate() === day;
  if (!real) return null;

  return `${String(year).padStart(4, '0')}-${String(month).padStart(2, '0')}-${String(day).padStart(2, '0')}`;
}

function convert(
  column: PasteColumn,
  raw: string,
): { value: PermitCellValue; reason: PasteIssueReason | null } {
  const text = raw.trim();
  if (text === '') return { value: null, reason: null };

  switch (column.type) {
    case 'number': {
      const value = parseLocaleNumber(text);
      return value === null ? { value: null, reason: 'number' } : { value, reason: null };
    }
    case 'bool': {
      const value = pastedBool(text);
      return value === null ? { value: null, reason: 'bool' } : { value: value === 'true', reason: null };
    }
    case 'date': {
      const value = parseLocaleDate(text);
      return value === null ? { value: null, reason: 'date' } : { value, reason: null };
    }
    default:
      return { value: text, reason: null };
  }
}

/** Розбирає вставлений блок у рядки за впорядкованими стовпцями. Порожні рядки пропускаються. */
export function parsePastedRows(text: string, columns: readonly PasteColumn[]): PasteResult {
  const rows: PastedRow[] = [];
  const issues: PasteIssue[] = [];
  let extraCells = 0;

  for (const line of parseBlock(text)) {
    if (line.every((cell) => cell.trim() === '')) continue;

    const rowNumber = rows.length + 1;
    const values: Record<string, PermitCellValue> = {};

    columns.forEach((column, index) => {
      const raw = line[index] ?? '';
      const { value, reason } = convert(column, raw);
      values[column.code] = value;
      if (reason !== null) issues.push({ row: rowNumber, column: column.code, text: raw.trim(), reason });
    });

    extraCells += Math.max(0, line.length - columns.length);
    rows.push({ values });
  }

  return { rows, issues, extraCells };
}
