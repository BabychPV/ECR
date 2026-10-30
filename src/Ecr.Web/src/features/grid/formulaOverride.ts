import type { TableSliceDto } from '@/api/types';
import type { components } from '@/api/schema';
import { t } from '@/shared/i18n';
import { formatDateTime } from '@/shared/format/datetime';

/**
 * ✎ D-461: «ручне значення замість формули» на сітці.
 *
 * ⚠ Клієнт НЕ вирішує доступ (коментар `permissions.ts`): що можна, каже сервер — `slice.formulaOverride`
 * (`canOverride`, `overridableColumns`, `overridableCells`, `manualCells`) і `cellPermissions` (відмова з іншої причини
 * стоїть там і перевіряється ПЕРШОЮ). Тут лише читання цих фактів і правила форми запиту:
 * <list>
 * — нове значення в комірці формули, що ЗАРАЗ дозволяє ручне, — лише з причиною (`overrideReason`);
 * — видалення в ручній комірці — повернення формули (причина не потрібна);
 * — видалення в комірці формули без ручного значення — нічого (сервер теж нічого не робить, а сітка показала б порожнечу);
 * — ручна комірка, чия формула вже НЕ дозволяє ручного, — лише повернення формули (нове значення сервер відхилить 403).
 * </list>
 */

export type FormulaManualCell = components['schemas']['FormulaManualCellDto'];
type FormulaOverrideSlice = components['schemas']['FormulaOverrideSliceDto'];

/** Межа причини — та сама, що на сервері (`CellFormulaOverride.MaxReasonLength`). */
export const OverrideReasonMaxLength = 1000;

interface Index {
  readonly columns: ReadonlySet<string>;
  readonly cells: ReadonlySet<string>;
}

// ⚠ `CL-03`: колбеки сітки питають на КОЖНУ видиму комірку — множини будуються раз на об'єкт зрізу.
const indexes = new WeakMap<FormulaOverrideSlice, Index>();

function indexOf(info: FormulaOverrideSlice): Index {
  let index = indexes.get(info);
  if (index === undefined) {
    index = { columns: new Set(info.overridableColumns), cells: new Set(info.overridableCells) };
    indexes.set(info, index);
  }

  return index;
}

function keyOf(rowKey: string, columnCode: string): string {
  return `${rowKey}:${columnCode}`;
}

/** Ручне значення комірки (причина, хто, коли) або `null`. Видно й без права. */
export function manualCellOf(slice: Pick<TableSliceDto, 'formulaOverride'>, rowKey: string, columnCode: string): FormulaManualCell | null {
  return slice.formulaOverride?.manualCells[keyOf(rowKey, columnCode)] ?? null;
}

/** Формула комірки ЗАРАЗ дозволяє ручне значення (прапорець увімкнено) — незалежно від права людини. */
export function allowsManual(slice: Pick<TableSliceDto, 'formulaOverride'>, rowKey: string, columnCode: string): boolean {
  const info = slice.formulaOverride;
  if (info === null || info === undefined) return false;

  const index = indexOf(info);

  return index.columns.has(columnCode) || index.cells.has(keyOf(rowKey, columnCode));
}

/**
 * Людина може писати в цю комірку формули як «ручне значення» (поставити чи повернути формулу):
 * право є, і формула дозволяє або комірка вже ручна. Інші причини відмови (`cellPermissions`) — окремо, раніше.
 */
export function overridesFormula(slice: Pick<TableSliceDto, 'formulaOverride'>, rowKey: string, columnCode: string): boolean {
  if (slice.formulaOverride?.canOverride !== true) return false;

  return allowsManual(slice, rowKey, columnCode) || manualCellOf(slice, rowKey, columnCode) !== null;
}

/** Що робити з правкою комірки формули. */
export type OverrideIntent =
  /** Не комірка формули з ручним значенням — звичайна правка. */
  | 'none'
  /** Нове ручне значення: потрібна причина. */
  | 'reason'
  /** Повернути формулу (видалення в ручній комірці). */
  | 'restore'
  /** Видалення в комірці, яку й так рахує формула, — нічого. */
  | 'noop'
  /** Ручна комірка, формула якої вже не дозволяє ручного, — лише повернення формули. */
  | 'restoreOnly';

/** Чи значення означає «стерти» (порожньо). */
export function isEmptyValue(value: unknown): boolean {
  return value === null || value === undefined || (typeof value === 'string' && value.trim().length === 0);
}

/** Намір правки комірки (`value` — уже приведене значення, `null` — стерти). */
export function overrideIntentOf(
  slice: Pick<TableSliceDto, 'formulaOverride'>,
  rowKey: string,
  columnCode: string,
  value: unknown,
): OverrideIntent {
  if (!overridesFormula(slice, rowKey, columnCode)) return 'none';

  const manual = manualCellOf(slice, rowKey, columnCode) !== null;
  if (isEmptyValue(value)) return manual ? 'restore' : 'noop';

  return allowsManual(slice, rowKey, columnCode) ? 'reason' : 'restoreOnly';
}

/**
 * Причина з поля діалогу в тому вигляді, що піде на сервер, — або `null`, якщо сервер її відхилить
 * (`ValidReason`: обрізана, непорожня, ≤ 1000, без керівних символів, крім переносу рядка).
 */
export function normalizeOverrideReason(raw: string): string | null {
  const reason = raw.replace(/\r\n/g, '\n').trim();
  if (reason.length === 0 || reason.length > OverrideReasonMaxLength) return null;

  for (const char of reason) {
    const code = char.codePointAt(0) ?? 0;
    const control = code < 0x20 || (code >= 0x7f && code <= 0x9f);
    if (control && char !== '\n') return null;
  }

  return reason;
}

/** Підказка ручної комірки: «Ручне значення · причина · хто, коли». */
export function manualCellHint(cell: FormulaManualCell): string {
  return t('grid.formulaOverride.hint', {
    reason: cell.reason,
    author: cell.setBy ?? t('grid.formulaOverride.unknownAuthor'),
    when: formatDateTime(cell.setAt),
  });
}

/** Пакет правок (вставка, протягування), розкладений за `overrideIntentOf`. */
export interface OverrideSplit<T> {
  /** Що надсилати (звичайні правки, повернення формули, нові ручні значення). */
  readonly kept: T[];
  /** Комірки нових ручних значень — потрібна ОДНА причина на пакет. */
  readonly reasonCells: T[];
  /** Відкинуто: видалення в комірці, яку й так рахує формула (нічого не змінює). */
  readonly noop: T[];
  /** Відкинуто: нове значення в ручній комірці, формула якої вже не дозволяє ручного. */
  readonly restoreOnly: T[];
}

/** Розкладає пакет правок за наміром (`overrideIntentOf`); без формул у таблиці — усе в `kept`. */
export function splitOverrideEdits<T extends { rowKey: string; columnCode: string; value: unknown; isEmpty?: boolean }>(
  slice: Pick<TableSliceDto, 'formulaOverride'>,
  edits: readonly T[],
): OverrideSplit<T> {
  if (slice.formulaOverride?.canOverride !== true) return { kept: [...edits], reasonCells: [], noop: [], restoreOnly: [] };

  const kept: T[] = [];
  const reasonCells: T[] = [];
  const noop: T[] = [];
  const restoreOnly: T[] = [];

  for (const edit of edits) {
    const intent = overrideIntentOf(slice, edit.rowKey, edit.columnCode, edit.isEmpty === true ? null : edit.value);
    if (intent === 'noop') noop.push(edit);
    else if (intent === 'restoreOnly') restoreOnly.push(edit);
    else {
      kept.push(edit);
      if (intent === 'reason') reasonCells.push(edit);
    }
  }

  return { kept, reasonCells, noop, restoreOnly };
}

/**
 * Ручні комірки виділення, які людина може повернути до формули (кнопка «Повернути формулу»).
 * Індекси діапазону — сітки (`labelOffset` — колонка підписів рядків), як у `colourableCells`.
 */
export function restorableCellsIn(
  slice: Pick<TableSliceDto, 'formulaOverride' | 'rows' | 'columns'>,
  range: { fromRow: number; toRow: number; fromColumn: number; toColumn: number } | null,
  labelOffset: number,
): { rowKey: string; columnCode: string }[] {
  const info = slice.formulaOverride;
  if (range === null || info === null || info === undefined || !info.canOverride) return [];
  if (Object.keys(info.manualCells).length === 0) return [];

  const cells: { rowKey: string; columnCode: string }[] = [];
  const toRow = Math.min(slice.rows.length - 1, range.toRow);
  const fromColumn = Math.max(0, range.fromColumn - labelOffset);
  const toColumn = Math.min(slice.columns.length - 1, range.toColumn - labelOffset);

  for (let r = Math.max(0, range.fromRow); r <= toRow; r++) {
    const row = slice.rows[r];
    if (row === undefined) continue;

    for (let c = fromColumn; c <= toColumn; c++) {
      const column = slice.columns[c];
      if (column !== undefined && info.manualCells[keyOf(row.rowKey, column.code)] !== undefined) {
        cells.push({ rowKey: row.rowKey, columnCode: column.code });
      }
    }
  }

  return cells;
}
