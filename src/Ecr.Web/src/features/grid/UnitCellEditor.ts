import type { UnitRef } from '@/api/types';
import { t } from '@/shared/i18n';
import { createListCellEditor, type ListOption } from './listCellEditor';

/**
 * Редактор комірки `CellDataType.Unit` — вибір одиниці з довідника (`R-01`).
 *
 * ⛔ Доти окремого редактора в `Unit` не було зовсім: звичайне текстове поле, у
 * яке оператор набирав `kg`, а сервер відмовляв «expects the identifier» —
 * комірка зберігає ІДЕНТИФІКАТОР одиниці (`ValueUnitId`), і знати його людина
 * не зобов'язана. Тепер це той самий редактор-список, що й у `Lookup`
 * (`listCellEditor.ts`): код одиниці, розмірність підказкою, пошук за обома.
 *
 * ⚠ `null` — перелік одиниць ще їде: «завантаження», а не порожній список.
 */
export function createUnitCellEditor(units: readonly UnitRef[] | null) {
  return createListCellEditor(
    () => ({ options: units === null ? null : unitOptionsOf(units) }),
    t('grid.unitEditorLabel'),
  );
}

/** Варіанти вибору одиниці: «очистити» плюс по одному на одиницю. */
export function unitOptionsOf(units: readonly UnitRef[]): readonly ListOption[] {
  const known = optionsCache.get(units);
  if (known !== undefined) return known;

  const built: ListOption[] = [
    { value: null, label: t('grid.listClear') },
    ...units.map((unit) => ({ value: String(unit.id), label: unit.code, hint: unit.dimensionCode })),
  ];
  optionsCache.set(units, built);

  return built;
}

const optionsCache = new WeakMap<readonly UnitRef[], readonly ListOption[]>();

/**
 * Показ одиниці поза редагуванням — її код (`kg`), а не ідентифікатор.
 *
 * ⚠ Одиниці, якої в переліку немає (перелік ще не приїхав, одиницю видалили),
 * показується те, що лежить у комірці: дані є, і ховати їх не можна.
 */
export function unitCellDisplay(value: unknown, units: readonly UnitRef[]): string {
  if (value === null || value === undefined || value === '') return '';

  const id = Number(value);
  if (!Number.isFinite(id)) return String(value);

  return codeIndexOf(units).get(id) ?? String(value);
}

/**
 * Ідентифікатор одиниці за її кодом — для вставки з Excel, де в комірці
 * стоїть `kg`, а не номер (`R-01`).
 *
 * @returns `null` — такої одиниці немає; тоді значення їде як є і вирішує
 * сервер (`422`).
 */
export function unitIdOfCode(text: string, units: readonly UnitRef[]): number | null {
  const wanted = text.trim().toLowerCase();
  if (wanted.length === 0) return null;

  return units.find((unit) => unit.code.toLowerCase() === wanted)?.id ?? null;
}

const codeCache = new WeakMap<readonly UnitRef[], ReadonlyMap<number, string>>();

function codeIndexOf(units: readonly UnitRef[]): ReadonlyMap<number, string> {
  const known = codeCache.get(units);
  if (known !== undefined) return known;

  const index = new Map(units.map((unit) => [unit.id, unit.code] as const));
  codeCache.set(units, index);

  return index;
}
