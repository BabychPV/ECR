import { apiFetch } from "@/api/client";
import type { components } from "@/api/schema";

/**
 * Серверне збереження правил умовного форматування версії шаблону (ФВ-2.6/2.7,
 * `cfg.ConditionalFormatRule`). Модель правила на клієнті — `conditionalFormat.ts`
 * (`ConditionalRule`); тут лише транспорт, без React-залежностей, щоб не
 * розширювати бандл `DocumentPage` (споживач — лінивий редактор конструктора).
 *
 * `PUT` замінює набір цілком: порядок у колонці — порядок у списку. Заморожена
 * версія відповідає `409 ECR-TMPL-0409`, невалідне правило — `422 ECR-CFG-0422`.
 */
export type ConditionalFormatRuleDto =
  components["schemas"]["ConditionalFormatRuleDto"];

const url = (templateVersionId: number): string =>
  `/api/v1/template-versions/${String(templateVersionId)}/conditional-formats`;

/** Правила версії. Право `Template.View`. */
export function getConditionalFormats(
  templateVersionId: number,
): Promise<ConditionalFormatRuleDto[]> {
  return apiFetch<ConditionalFormatRuleDto[]>(url(templateVersionId));
}

/** Замінює набір правил версії-чернетки. Право `Template.Edit`. */
export function saveConditionalFormats(
  templateVersionId: number,
  rules: readonly ConditionalFormatRuleDto[],
): Promise<ConditionalFormatRuleDto[]> {
  return apiFetch<ConditionalFormatRuleDto[]>(url(templateVersionId), {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ rules }),
  });
}
