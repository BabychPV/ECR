/*
 * Рівні гранта — окремо від `SheetActions.tsx` (D-132, бюджет маршрутів): перевірка гранта на проєкт
 * (`hasProjectGrant`) потрібна й адміністративним сторінкам, яким увесь рядок дій аркуша не потрібен.
 * `SheetActions.tsx` реекспортує все звідси — наявні імпорти не змінюються.
 */

/**
 * Рівні гранта — **дзеркало** `Ecr.Domain.Enums.GrantLevel`, у тому самому
 * порядку зростання (`None`=0 … `Manage`=5).
 *
 * ⛔ Порядок і є правилом: `EditRules.CanSubmit` вимагає `>= Submit` (3), а
 * `EditRules.CanApprove` — `>= Approve` (4). Переставити тут два імені —
 * означає показати кнопку тому, кому сервер відмовить, і сховати в того, хто
 * має право.
 */
export const GrantOrder = ['None', 'Read', 'Write', 'Submit', 'Approve', 'Manage'] as const;

/** Назва рівня гранта, як її віддає `/api/v1/me` (`GetCurrentUserHandler`). */
export type GrantLevelName = (typeof GrantOrder)[number];

/** Чи дотягує наявний рівень до потрібного. */
export function meetsGrant(actual: GrantLevelName, required: GrantLevelName): boolean {
  return GrantOrder.indexOf(actual) >= GrantOrder.indexOf(required);
}
