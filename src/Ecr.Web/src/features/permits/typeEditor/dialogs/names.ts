import type { UnitRef } from '@/api/types';
import { DefaultLanguage, language } from '@/shared/i18n';
import { displayName } from './ops';

/**
 * Назва мовою користувача: його мова → англійська → перша наявна (`displayName`).
 *
 * ⚠ Той самий ланцюг запасних варіантів, що в `shared/i18n/localized`, але для словника
 * `{ мова: текст }`, який віддає `PermitTypeDto` (там об'єкт-обгортка `{ values }`).
 */
export function pickName(names: Readonly<Record<string, string>>): string {
  const lang = language();
  const exact = names[lang];
  if (exact !== undefined && exact.length > 0) return exact;

  const fallback = names[DefaultLanguage];
  if (fallback !== undefined && fallback.length > 0) return fallback;

  return displayName(names);
}

/** Одиниця для вибору у формі діалогу. */
export interface UnitOption {
  readonly id: number;
  readonly code: string;
  readonly label: string;
}

/** Код одиниці «тис. м³», яку мокап ставить серії місяців (сід `thousand_m3`). */
export const MonthSeriesUnitCode = 'thousand_m3';

/** Одиниці для вибору: позначення мовою користувача, інакше код. */
export function toUnitOptions(units: readonly UnitRef[]): UnitOption[] {
  return units.map((unit) => ({
    id: unit.id,
    code: unit.code,
    label: unit.symbolL10n != null ? pickName(unit.symbolL10n) || unit.code : unit.code,
  }));
}

/** Одиниця серії місяців; `null`, якщо в довіднику її немає. */
export function findMonthUnit(options: readonly UnitOption[]): UnitOption | null {
  return options.find((option) => option.code === MonthSeriesUnitCode) ?? null;
}
