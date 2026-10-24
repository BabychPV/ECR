import type { UnitRef } from '@/api/types';
import { DefaultLanguage, language } from '@/shared/i18n';

/*
 * Назви мовою користувача — окремо від `model.ts` (D-132, бюджет маршруту `PermitPage`): шапка й Overview беруть
 * лише їх, а `model.ts` тягне розбір вставки (`pasteRows` → `rc812/rowModel`), потрібний лише вкладці таблиці.
 * `model.ts` реекспортує обидві функції — наявні імпорти не змінюються.
 */

/** Назва мовою користувача: його мова → англійська → перша наявна. */
export function localName(names: Readonly<Record<string, string>>): string {
  const exact = names[language()];
  if (exact !== undefined && exact !== '') return exact;
  const fallback = names[DefaultLanguage];
  if (fallback !== undefined && fallback !== '') return fallback;
  return Object.values(names).find((value) => value !== '') ?? '';
}

/** Підписи одиниць за id (позначення мовою користувача, інакше код). */
export function unitLabels(units: readonly UnitRef[]): ReadonlyMap<number, string> {
  return new Map(
    units.map((unit) => [unit.id, unit.symbolL10n != null ? localName(unit.symbolL10n) || unit.code : unit.code]),
  );
}
