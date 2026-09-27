import type { TableSliceDto } from '@/api/types';
import { captureEdit, revertsToSaved, type CapturedEdit, type EditSignal } from './edits';
import { rowIndexOf } from './rowIndex';

/**
 * Діапазонна форма `afteredit` — протягування маркером заповнення (fill handle).
 *
 * ⛔ RevoGrid шле `afteredit` у ДВОХ формах. Одиночна (`onCellEdit`) несе
 * `{ prop, model, val, … }`. Діапазонна (`onRangeEdit`, `revo-grid.entry.js`)
 * несе `{ data, models, type }`: `data` — `{ [індекс рядка]: { [prop]: значення } }`
 * (`ColumnService.getRangeData`, значення — сире значення моделі
 * рядка-джерела), `models` — `{ [індекс рядка]: модель рядка }`
 * (`collectModelsOfRange`), і НЕМАЄ ні `prop`, ні `val`. Обробник знав лише
 * першу — протягнуте малювалось у сітці й не доходило ні до сховища, ні до
 * PATCH.
 */
export interface RangeEditDetail {
  data: Record<string, Record<string, unknown> | undefined>;
  models: Record<string, unknown>;
}

export function isRangeEdit(detail: unknown): detail is RangeEditDetail {
  if (typeof detail !== 'object' || detail === null) return false;

  const candidate = detail as { prop?: unknown; data?: unknown; models?: unknown };

  return (
    candidate.prop === undefined &&
    typeof candidate.data === 'object' &&
    candidate.data !== null &&
    typeof candidate.models === 'object' &&
    candidate.models !== null
  );
}

/** Результат розбору діапазону: що зберегти і що повернулось до збереженого. */
export interface CapturedRange {
  captured: CapturedEdit[];
  /** Комірки, чиє нове значення дорівнює збереженому (`V-01`, скасування правки). */
  reverted: EditSignal[];
}

/**
 * Розкладає діапазон на поштучні правки тим самим `captureEdit`, що й ручне
 * введення: приведення типу, право (`decide`), read-only і «нічого не змінилось»
 * — одні правила для всіх джерел правки.
 *
 * ⚠ Значення — сире значення моделі (для `Int` — число, для `Decimal` — рядок),
 * тож воно йде через `String(...)`, як `val` одиночної форми; `null` стає `''` і
 * `coerce` повертає його в `null`.
 */
export function captureRange(
  slice: TableSliceDto,
  detail: RangeEditDetail,
  rowKeyOf: (model: unknown) => string,
): CapturedRange {
  const rows = rowIndexOf(slice);
  const captured: CapturedEdit[] = [];
  const reverted: EditSignal[] = [];

  for (const [index, cells] of Object.entries(detail.data)) {
    if (cells === undefined) continue;

    const rowKey = rowKeyOf(detail.models[index]);

    for (const [columnCode, value] of Object.entries(cells)) {
      const signal: EditSignal = { rowKey, columnCode, raw: value == null ? '' : String(value) };
      const edit = captureEdit(slice, signal, rows);

      if (edit !== null) captured.push(edit);
      else if (revertsToSaved(slice, signal, rows)) reverted.push(signal);
    }
  }

  return { captured, reverted };
}
