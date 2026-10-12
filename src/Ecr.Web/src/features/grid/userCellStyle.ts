import type { CellFormatDto, CellStyleChange, TableSliceDto } from '@/api/types';
import { isHexColour } from '@/shared/theme/cellPalette';
import { withCellFormat } from './conditionalAppearance';
import { cellKey } from './permissions';
import type { GridSelection } from './selection';

/**
 * Користувацький колір комірки «як в Excel» (L4): заливка, колір тексту і жирність,
 * які людина ставить на виділене з тулбара сітки.
 *
 * Контракт (CELL-STYLE L3a, `schema.d.ts`): `TableSliceDto.cellStyles` — ключ
 * `"{rowKey}:{columnCode}"`, значення — `CellFormatDto`; запис —
 * `PATCH /documents/{id}/tables/{tid}/cell-styles` з `PatchCellStylesRequest`.
 *
 * Шар: колонка < рядок < користувацький < УМОВНИЙ ФОРМАТ < стан (рішення Аудиту
 * 12.10, як в Excel: правило перекриває ручну заливку). Далі все йде тим самим
 * шляхом, що стиль автора і CF (`cellAppearanceOf`): контраст під обидві теми,
 * заливка лише там, де її дозволяє `cellEditors.css`.
 */
export type UserCellFormat = Pick<CellFormatDto, 'backgroundHex' | 'foregroundHex' | 'isBold'>;

/** Одна комірка запису: `undefined`/`null` — не чіпати; `""` — зняти; `#rrggbb` — поставити. */
export type CellStylePatch = Pick<CellStyleChange, 'rowKey' | 'columnCode' | 'backgroundHex' | 'foregroundHex' | 'isBold'>;

/** Стеля сервера на комірок в одному запиті (`PatchCellStylesRequest.MaxItems`). */
export const CellStyleServerMaxItems = 2000;

/** Розмір порції запису: з запасом під стелю сервера, порції йдуть послідовно. */
export const CellStyleChunkSize = 1000;

export function chunkPatches<T>(items: readonly T[], size: number = CellStyleChunkSize): T[][] {
  const chunks: T[][] = [];

  for (let i = 0; i < items.length; i += size) chunks.push(items.slice(i, i + size));

  return chunks;
}

/** Колір у запиті — лише `#rrggbb`, `""` (зняти) або «не чіпати». */
export function isValidPatch(patch: CellStylePatch): boolean {
  return [patch.backgroundHex, patch.foregroundHex].every(
    (color) => color === undefined || color === null || color === '' || isHexColour(color),
  );
}

export function userStyleOf(slice: Pick<TableSliceDto, 'cellStyles'>, key: string): UserCellFormat | null {
  const found = slice.cellStyles?.[key];

  if (found === undefined) return null;

  return found.backgroundHex === null && found.foregroundHex === null && !found.isBold ? null : found;
}

/** Стиль із накладеним користувацьким оформленням (колір, жирність — із сервера). */
export function withUserStyle(
  style: Parameters<typeof withCellFormat>[0],
  format: UserCellFormat,
): ReturnType<typeof withCellFormat> {
  return withCellFormat(style, {
    backgroundHex: format.backgroundHex,
    foregroundHex: format.foregroundHex,
    isBold: format.isBold,
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

/** Кеш зрізу після оптимістичного застосування запису (семантика сервера: `null` — не чіпати, `""` — зняти). */
export function applyStylePatches(slice: TableSliceDto, patches: readonly CellStylePatch[]): TableSliceDto {
  const next: Record<string, UserCellFormat> = { ...(slice.cellStyles ?? {}) };

  for (const patch of patches) {
    const key = cellKey(patch.rowKey, patch.columnCode);
    const before: UserCellFormat = next[key] ?? { backgroundHex: null, foregroundHex: null, isBold: false };
    const pick = (value: string | null | undefined, old: string | null): string | null =>
      value === undefined || value === null ? old : value === '' ? null : value;
    const merged: UserCellFormat = {
      backgroundHex: pick(patch.backgroundHex, before.backgroundHex),
      foregroundHex: pick(patch.foregroundHex, before.foregroundHex),
      isBold: patch.isBold ?? before.isBold,
    };

    if (merged.backgroundHex === null && merged.foregroundHex === null && !merged.isBold) delete next[key];
    else next[key] = merged;
  }

  return { ...slice, cellStyles: next };
}

export { StandardColours, ThemeColours, ThemeShades, isHexColour } from '@/shared/theme/cellPalette';
