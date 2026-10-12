import type { TableSliceDto } from '@/api/types';
import { withCellFormat } from './conditionalAppearance';
import { cellKey } from './permissions';
import type { GridSelection } from './selection';

/**
 * Користувацький колір комірки «як в Excel» (L4): заливка і колір тексту, які
 * людина ставить на виділене з тулбара сітки.
 *
 * Контракт (L3a, сервер): `TableSliceDto.cellStyles` — ключ `"{rowKey}:{columnCode}"`,
 * значення — `{ backgroundHex, foregroundHex }` (`#rrggbb` або `null` — колір
 * не задано); запис — `PATCH /documents/{id}/tables/{tid}/cell-styles`.
 *
 * ⚠ Поки поля немає у згенерованому `schema.d.ts`, клієнт читає його через
 * власний необов'язковий тип: гілка збирається і до, і після сервера, а відсутнє
 * поле означає «користувацьких кольорів немає».
 *
 * ⛔ Шар ПІСЛЯ умовного форматування: що людина пофарбувала сама — сильніше за
 * правило. Далі все йде тим самим шляхом, що стиль автора і CF
 * (`cellAppearanceOf`): контраст під обидві теми, заливка лише там, де її
 * дозволяє `cellEditors.css`.
 */
export interface UserCellFormat {
  readonly backgroundHex?: string | null;
  readonly foregroundHex?: string | null;
}

export type SliceWithUserStyles = TableSliceDto & {
  cellStyles?: Readonly<Record<string, UserCellFormat>> | null;
};

/** Одна комірка запиту запису; поле, якого немає, не змінюється, `null` — скидає. */
export interface CellStylePatch {
  readonly rowKey: string;
  readonly columnCode: string;
  readonly backgroundHex?: string | null;
  readonly foregroundHex?: string | null;
}

export function userStyleOf(slice: Pick<SliceWithUserStyles, 'cellStyles'>, key: string): UserCellFormat | null {
  const found = slice.cellStyles?.[key];

  if (found === undefined) return null;

  return found.backgroundHex == null && found.foregroundHex == null ? null : found;
}

/** Стиль із накладеним користувацьким кольором; жирність не чіпається. */
export function withUserStyle(
  style: Parameters<typeof withCellFormat>[0],
  format: UserCellFormat,
): ReturnType<typeof withCellFormat> {
  return withCellFormat(style, {
    backgroundHex: format.backgroundHex ?? null,
    foregroundHex: format.foregroundHex ?? null,
    isBold: false,
  });
}

/**
 * Причини відмови, які на колір НЕ впливають: обчислена комірка і колонка «лише
 * читання» не редагуються, але фарбуються (як в Excel — захищений аркуш з
 * дозволом форматувати). Усе інше (немає запису, заборона, подано) колір теж
 * забороняє; останнє слово — за сервером.
 */
const StyleNeutralReasons: ReadonlySet<string> = new Set(['None', 'CalculatedCell', 'ColumnReadOnly']);

/**
 * Комірки виділеного прямокутника (індекси — у даних, не в сітці), на які в
 * людини є право запису. `range === null` — нічого не виділено.
 */
export function colourableCells(
  slice: TableSliceDto,
  range: GridSelection['range'] | null,
  labelOffset: number,
): { rowKey: string; columnCode: string }[] {
  if (range === null || slice.rows.length === 0 || slice.columns.length === 0) return [];

  const fromColumn = Math.max(0, range.fromColumn - labelOffset);
  const toColumn = Math.min(slice.columns.length - 1, range.toColumn - labelOffset);
  const toRow = Math.min(slice.rows.length - 1, range.toRow);
  const cells: { rowKey: string; columnCode: string }[] = [];

  for (let r = Math.max(0, range.fromRow); r <= toRow; r++) {
    const row = slice.rows[r];
    if (row === undefined) continue;

    for (let c = fromColumn; c <= toColumn; c++) {
      const column = slice.columns[c];
      if (column === undefined) continue;

      const permission = slice.cellPermissions[cellKey(row.rowKey, column.code)];
      if (permission === undefined || StyleNeutralReasons.has(permission)) {
        cells.push({ rowKey: row.rowKey, columnCode: column.code });
      }
    }
  }

  return cells;
}

/** Кеш зрізу після оптимістичного застосування запису. */
export function applyStylePatches(slice: SliceWithUserStyles, patches: readonly CellStylePatch[]): SliceWithUserStyles {
  const next: Record<string, UserCellFormat> = { ...(slice.cellStyles ?? {}) };

  for (const patch of patches) {
    const key = cellKey(patch.rowKey, patch.columnCode);
    const before = next[key] ?? {};
    const merged: UserCellFormat = {
      backgroundHex: patch.backgroundHex === undefined ? before.backgroundHex ?? null : patch.backgroundHex,
      foregroundHex: patch.foregroundHex === undefined ? before.foregroundHex ?? null : patch.foregroundHex,
    };

    if (merged.backgroundHex === null && merged.foregroundHex === null) delete next[key];
    else next[key] = merged;
  }

  return { ...slice, cellStyles: next };
}

export { StandardColours, ThemeColours, ThemeShades, isHexColour } from '@/shared/theme/cellPalette';
