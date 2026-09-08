import { apiFetch } from '@/api/client';
import type {
  CreatePeriodAccessRuleRequest,
  PeriodAccessRuleDto,
  UpdatePeriodAccessRuleRequest,
} from '@/api/types';
import type { CreatePeriodAccessRuleDraft, UpdatePeriodAccessRuleDraft } from './periodAccessRule';

/**
 * Звернення редактора правил доступу до періоду (`ФВ-2.15`, W5.4).
 *
 * ⛔ Три дії, а не PUT-за-кодом/DELETE, як у `sheetApi.ts`: у
 * `PeriodAccessRuleDef` немає поля `Code`, тож `POST` заводить нове правило,
 * а `PUT …/{id}` лише змінює наявне — докладна причина в `periodAccessRule.ts`.
 */

/** Заводить нове правило. */
export function createPeriodAccessRule(
  templateVersionId: number,
  draft: CreatePeriodAccessRuleDraft,
): Promise<PeriodAccessRuleDto> {
  return apiFetch<PeriodAccessRuleDto>(
    `/api/v1/template-versions/${String(templateVersionId)}/period-access-rules`,
    {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(createBody(draft)),
    },
  );
}

/** Змінює прив'язку й поведінку наявного правила. */
export function savePeriodAccessRule(
  templateVersionId: number,
  ruleId: number,
  draft: UpdatePeriodAccessRuleDraft,
): Promise<PeriodAccessRuleDto> {
  return apiFetch<PeriodAccessRuleDto>(
    `/api/v1/template-versions/${String(templateVersionId)}/period-access-rules/${String(ruleId)}`,
    {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(updateBody(draft)),
    },
  );
}

/** Прибирає правило (фізично). */
export function deletePeriodAccessRule(templateVersionId: number, ruleId: number): Promise<void> {
  return apiFetch<void>(
    `/api/v1/template-versions/${String(templateVersionId)}/period-access-rules/${String(ruleId)}`,
    { method: 'DELETE' },
  );
}

/** Тіло запиту `POST …/period-access-rules`. */
function createBody(draft: CreatePeriodAccessRuleDraft): CreatePeriodAccessRuleRequest {
  return {
    ruleKind: draft.ruleKind,
    onOutOfWindow: draft.onOutOfWindow,
    sheetDefId: draft.sheetDefId,
    tableDefId: draft.tableDefId,
    roleId: draft.roleId,
    rowKind: draft.rowKind,
    fromSequence: draft.fromSequence,
    toSequence: draft.toSequence,
    sourceColumnDefId: draft.sourceColumnDefId,
    relativeOffset: draft.relativeOffset,
    conditionExpr: draft.conditionExpr.trim().length === 0 ? null : draft.conditionExpr.trim(),
  };
}

/** Тіло запиту `PUT …/period-access-rules/{id}`. */
function updateBody(draft: UpdatePeriodAccessRuleDraft): UpdatePeriodAccessRuleRequest {
  return {
    onOutOfWindow: draft.onOutOfWindow,
    sheetDefId: draft.sheetDefId,
    tableDefId: draft.tableDefId,
    roleId: draft.roleId,
    rowKind: draft.rowKind,
  };
}
