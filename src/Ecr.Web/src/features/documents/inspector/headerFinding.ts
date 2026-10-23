import type { ValidationFindingDto } from '@/api/types';

/*
 * ⚠ Окремим модулем, а не в `inspectorModel.ts` (`D-132`): `DocumentPage` потребує
 * лише цієї перевірки, а статичний імпорт моделі інспектора тягнув у чанк маршруту
 * весь модуль, хоча сам інспектор — лінивий чанк.
 */

/** Правило знахідки шапки документа (`tableDefId` 0) — веде до панелі шапки. */
export const HeaderFindingRule = 'ECR-HDR-0422';

/** Знахідка шапки документа: адреса — панель шапки, а не клітинка. */
export function isHeaderFinding(finding: ValidationFindingDto): boolean {
  return finding.tableDefId === 0 && finding.ruleCode === HeaderFindingRule;
}
