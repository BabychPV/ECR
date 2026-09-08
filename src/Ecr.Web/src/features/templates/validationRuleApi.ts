import { apiFetch } from '@/api/client';
import type { SaveValidationRuleRequest, ValidationRuleDto } from '@/api/types';
import type { ValidationRuleDraft } from './validationRule';

/**
 * Звернення редактора правил валідації (W5.4, продовження `ФВ-2.1` на
 * `ValidationRule`, за зразком `sheetApi.ts`).
 *
 * ⚠ Правило адресується ТАБЛИЦЕЮ (`tableDefId`) і кодом, а не лише кодом:
 * код правила унікальний лише в межах однієї таблиці
 * (`UQ_ValidationRule` на `(TableDefId, Code)`).
 */

/**
 * Записує правило; створює його, якщо коду в цій таблиці ще немає.
 *
 * ⚠ `PUT`, а не `POST`: адресою правила є код у межах таблиці, і задає його
 * викликач (`D2-147`) — та сама форма, що й `saveSheet`.
 */
export function saveValidationRule(
  templateVersionId: number,
  tableDefId: number,
  draft: ValidationRuleDraft,
): Promise<ValidationRuleDto> {
  return apiFetch<ValidationRuleDto>(
    `/api/v1/template-versions/${String(templateVersionId)}/tables/${String(tableDefId)}/validation-rules/${encodeURIComponent(draft.code)}`,
    {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(ruleBody(draft)),
    },
  );
}

/** Прибирає правило з таблиці чернетки (фізично). */
export function deleteValidationRule(
  templateVersionId: number,
  tableDefId: number,
  code: string,
): Promise<void> {
  return apiFetch<void>(
    `/api/v1/template-versions/${String(templateVersionId)}/tables/${String(tableDefId)}/validation-rules/${encodeURIComponent(code)}`,
    { method: 'DELETE' },
  );
}

/** Тіло запиту `PUT …/validation-rules/{code}`. */
function ruleBody(draft: ValidationRuleDraft): SaveValidationRuleRequest {
  return {
    severity: draft.severity,
    scope: draft.scope,
    expression: draft.expression.trim(),
    messageL10n: draft.messageL10n,
    columnDefId: draft.columnDefId,
    isActive: draft.isActive,
  };
}
