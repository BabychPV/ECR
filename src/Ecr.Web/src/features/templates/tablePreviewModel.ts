import type { TableDto } from '@/api/types';
import type { ConditionalFormatRuleDto } from './conditionalFormatApi';
import { AA, contrast } from '@/shared/theme/contrast';
import { themeSurface } from '@/shared/theme/theme';
import {
  firstMatchingRule,
  ruleFromWire,
  whyRuleIncomplete,
  type ConditionalRule,
} from './conditionalFormat';

/**
 * Попередній перегляд таблиці шаблону в конструкторі (`ФВ-2.6`): як виглядатиме
 * таблиця з поточним порядком колонок і рядків, типами, одиницями й умовним
 * форматуванням за правилами (`ФВ-2.7`). Лише модель — без React, щоб її можна
 * було перевірити без DOM; рендер — `TablePreview.tsx` (⚠ інше ім'я, ніж `tablePreview`: Windows-збірка не розрізняє регістр) (лінивий чанк).
 *
 * ⛔ Перегляд нічого не пише: читає структуру версії (`GET …/structure`, уже в
 * кеші сторінки) і правила (`GET …/conditional-formats`).
 *
 * ⚠ Правила перевіряються тим самим `firstMatchingRule`, що й у редакторі
 * правил: перше повне правило колонки, яке спрацьовує, виграє.
 */

/** Колонка перегляду — у порядку, в якому її покаже сітка. */
export interface PreviewColumn {
  readonly code: string;
  readonly label: string;
  readonly dataType: string;
  readonly unitSymbol: string | null;
  readonly isRequired: boolean;
  readonly isReadOnly: boolean;
  /** Повні правила колонки в порядку пріоритету. */
  readonly rules: readonly ConditionalRule[];
}

/** Рядок перегляду; `depth` — вкладеність за `parentRowKey` (0 — корінь). */
export interface PreviewRow {
  readonly rowKey: string;
  readonly label: string;
  readonly rowKind: string;
  readonly isReadOnly: boolean;
  readonly depth: number;
}

export interface TablePreviewModel {
  readonly columns: readonly PreviewColumn[];
  /** Приховані колонки: у сітці їх не видно, тож у перегляді їх лише злічено. */
  readonly hiddenColumns: number;
  readonly rows: readonly PreviewRow[];
  /** Рядків більше, ніж показано (`MaxPreviewRows`). */
  readonly truncatedRows: number;
  /**
   * Правила колонок ЦІЄЇ таблиці, яких перегляд не показує: колонка прихована,
   * оператор невідомий або правило неповне. Правила інших таблиць версії не
   * рахуються — набір правил належить версії, а не таблиці.
   */
  readonly ignoredRules: number;
}

/**
 * Межа рядків у перегляді. ⚠ Перегляд — про вигляд, а не про повний перелік:
 * тисяча рядків у діалозі не додала б нічого, крім затримки відкриття.
 */
export const MaxPreviewRows = 200;

function byOrdinal<T extends { readonly ordinal: number }>(key: (item: T) => string) {
  return (a: T, b: T): number => a.ordinal - b.ordinal || key(a).localeCompare(key(b));
}

/**
 * Модель перегляду таблиці.
 *
 * @param label Підпис колонки/рядка мовою інтерфейсу (`localized`), передається
 *   ззовні, щоб модуль не залежав від поточної мови.
 */
export function buildTablePreview(
  table: Pick<TableDto, 'columns' | 'rows'>,
  rules: readonly ConditionalFormatRuleDto[],
  label: (l10n: TableDto['columns'][number]['headerL10n']) => string,
): TablePreviewModel {
  const visible = table.columns
    .filter((column) => !column.isHidden)
    .sort(byOrdinal((column) => column.code));
  const codes = new Set(visible.map((column) => column.code));
  const own = rules.filter((rule) => table.columns.some((column) => column.code === rule.columnCode));

  const complete = own
    .map(ruleFromWire)
    .filter((rule): rule is ConditionalRule => rule !== null && whyRuleIncomplete(rule) === null);
  const applied = complete.filter((rule) => codes.has(rule.columnCode));

  const columns = visible.map<PreviewColumn>((column) => ({
    code: column.code,
    label: label(column.headerL10n) || column.code,
    dataType: column.dataType,
    unitSymbol: column.unitSymbol,
    isRequired: column.isRequired,
    isReadOnly: column.isReadOnly,
    rules: applied.filter((rule) => rule.columnCode === column.code),
  }));

  const sortedRows = [...table.rows].sort(byOrdinal((row) => row.rowKey));
  const parentOf = new Map(sortedRows.map((row) => [row.rowKey, row.parentRowKey]));

  const depthOf = (rowKey: string): number => {
    // ⚠ Захист від циклу в даних: глибина не більша за кількість рядків.
    let depth = 0;
    let parent = parentOf.get(rowKey) ?? null;
    while (parent !== null && parentOf.has(parent) && depth < sortedRows.length) {
      depth += 1;
      parent = parentOf.get(parent) ?? null;
    }
    return depth;
  };

  const rows = sortedRows.slice(0, MaxPreviewRows).map<PreviewRow>((row) => ({
    rowKey: row.rowKey,
    label: row.label?.trim() ? row.label : row.rowKey,
    rowKind: row.rowKind,
    isReadOnly: row.isReadOnly,
    depth: depthOf(row.rowKey),
  }));

  return {
    columns,
    hiddenColumns: table.columns.length - visible.length,
    rows,
    truncatedRows: Math.max(0, sortedRows.length - MaxPreviewRows),
    ignoredRules: own.length - applied.length,
  };
}

/** Вигляд комірки за правилом — inline-стиль для перегляду. */
export interface CellLook {
  readonly backgroundColor?: string;
  readonly color?: string;
  readonly fontWeight?: 'bold';
}

const Hex = /^#[0-9a-fA-F]{6}$/;

/**
 * Як правило пофарбує комірку на поверхні теми `surface`.
 *
 * ⚠ Та сама політика читабельності, що в сітці (`cellAppearance.ts`, `X-10`):
 * колір тексту автора лише тоді, коли він читається (`AA.text`) на тому, що під
 * ним; інакше — колір тексту теми, що дає більший контраст із заливкою, або
 * жодного (колір теми), якщо заливки немає.
 */
export function cellLook(rule: ConditionalRule | null, surface: string): CellLook {
  if (rule === null) return {};

  const fill = Hex.test(rule.backgroundHex) ? rule.backgroundHex : null;
  const foreground = Hex.test(rule.foregroundHex) ? rule.foregroundHex : null;
  const under = fill ?? surface;

  let color: string | undefined;
  if (foreground !== null && contrast(foreground, under) >= AA.text) color = foreground;
  else if (fill !== null) color = bestTextOn(fill);

  return {
    ...(fill === null ? {} : { backgroundColor: fill }),
    ...(color === undefined ? {} : { color }),
    ...(rule.isBold ? { fontWeight: 'bold' as const } : {}),
  };
}

function bestTextOn(fill: string): string {
  const dark = themeSurface.light.text;
  const light = themeSurface.light.body;

  return contrast(dark, fill) >= contrast(light, fill) ? dark : light;
}

/** Вигляд комірки колонки зі значенням-прикладом (`null` — порожня комірка). */
export function previewCellLook(column: PreviewColumn, sample: string | null, surface: string): CellLook {
  return cellLook(firstMatchingRule(column.rules, column.code, sample), surface);
}
