import { apiFetchResponse, EcrApiError } from "@/api/client";
import type { components } from "@/api/schema";

/**
 * Серверне збереження правил умовного форматування версії шаблону (ФВ-2.6/2.7,
 * `cfg.ConditionalFormatRule`). Модель правила на клієнті — `conditionalFormat.ts`
 * (`ConditionalRule`); тут лише транспорт, без React-залежностей, щоб не
 * розширювати бандл `DocumentPage` (споживач — лінивий редактор конструктора).
 *
 * `PUT` замінює набір цілком: порядок у колонці — порядок у списку. Заморожена
 * версія відповідає `409 ECR-TMPL-0409`, невалідне правило — `422 ECR-CFG-0422`.
 *
 * ⛔ Версія набору: `GET` віддає її в `ETag`, `PUT` несе її в `If-Match`
 * (той самий контракт, що в гранта ролі, `GrantsPanel.tsx`). Набір змінили
 * після читання — `409 ECR-TMPL-0409` з актуальною версією в
 * `details.version` (`isStaleConditionalFormats`); без заголовка — `422`.
 */
export type ConditionalFormatRuleDto =
  components["schemas"]["ConditionalFormatRuleDto"];

/** Набір правил разом із версією з заголовка `ETag`. */
export interface ConditionalFormatSet {
  readonly rules: ConditionalFormatRuleDto[];
  readonly etag: string | null;
}

/** Ключ запиту правил версії: редактор, перегляд і збереження читають один кеш. */
export const conditionalFormatsKey = (templateVersionId: number) =>
  ["conditionalFormats", templateVersionId] as const;

const url = (templateVersionId: number): string =>
  `/api/v1/template-versions/${String(templateVersionId)}/conditional-formats`;

/** Правила версії. Право `Template.View`. */
export async function getConditionalFormats(
  templateVersionId: number,
): Promise<ConditionalFormatSet> {
  const response = await apiFetchResponse(url(templateVersionId));

  return {
    rules: (await response.json()) as ConditionalFormatRuleDto[],
    etag: response.headers.get("ETag"),
  };
}

/**
 * Замінює набір правил версії-чернетки. Право `Template.Edit`.
 *
 * @param etag Версія, з якої почалася ЦЯ правка (`ETag` читання), а не
 *   остання відповідь сервера: інакше збереження мовчки затерло б чужу правку,
 *   якої людина не бачила.
 */
export async function saveConditionalFormats(
  templateVersionId: number,
  rules: readonly ConditionalFormatRuleDto[],
  etag: string | null,
): Promise<ConditionalFormatSet> {
  const response = await apiFetchResponse(url(templateVersionId), {
    method: "PUT",
    headers: {
      "Content-Type": "application/json",
      ...(etag === null ? {} : { "If-Match": etag }),
    },
    body: JSON.stringify({ rules }),
  });

  return {
    rules: (await response.json()) as ConditionalFormatRuleDto[],
    etag: response.headers.get("ETag"),
  };
}

/**
 * Чи це відмова через застарілу версію набору (`ECR-TMPL-0409` з актуальною
 * версією в `details.version`), а не заморожена версія шаблону того самого коду.
 */
export function isStaleConditionalFormats(error: unknown): boolean {
  return (
    error instanceof EcrApiError &&
    error.problem.status === 409 &&
    error.problem.errorCode === "ECR-TMPL-0409" &&
    typeof error.problem.extensions2?.["version"] === "string"
  );
}
