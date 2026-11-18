import type { QueryClient } from '@tanstack/react-query';
import { localized } from '@/shared/i18n/localized';
import { t } from '@/shared/i18n';
import type { ApproverRoleDto, ScopedApprovalRouteDto } from './approvalRoutesApi';

/**
 * ✎ B155 (варіант C): правила кроків маршруту області на клієнті, ключі кешу й назви ролей.
 *
 * ⚠ Обидва правила кроків сервер перевіряє сам (`422 ECR-DOC-0422` `approvalRouteTooLong` /
 * `approvalRouteConsecutiveRole`). Тут вони — щоб людина не збирала маршрут, який однаково відхилять, і щоб
 * попередній підрахунок перезапуску (`preview`) не рахувався для неможливого набору.
 */
export const MaxApprovalSteps = 20;

/** Чому набір кроків не можна зберегти; `null` — можна. */
export type StepsProblem = 'tooLong' | 'consecutiveRole' | null;

/** Перевірка всього набору: довжина і та сама роль двічі поспіль (видалення кроку теж може це створити). */
export function stepsProblem(roleIds: readonly number[]): StepsProblem {
  if (roleIds.length > MaxApprovalSteps) return 'tooLong';

  for (let index = 1; index < roleIds.length; index += 1) {
    if (roleIds[index] === roleIds[index - 1]) return 'consecutiveRole';
  }

  return null;
}

/** Чи можна дописати крок із цією роллю в кінець. */
export function appendProblem(roleIds: readonly number[], roleId: number): StepsProblem {
  return stepsProblem([...roleIds, roleId]);
}

/**
 * Відповідь маршруту області в безпечній формі. ⚠ Розділ маршруту — сусід на чужих сторінках (картка шаблону,
 * безпека, налаштування проєкту): відповідь не тієї форми (інша версія сервера, проксі, заглушка) малює «маршруту
 * немає, лише читання», а не валить усю сторінку на `undefined.map`.
 */
export function normalizeRoute(raw: unknown): ScopedApprovalRouteDto {
  const value = (typeof raw === 'object' && raw !== null && !Array.isArray(raw) ? raw : {}) as Partial<ScopedApprovalRouteDto>;
  const steps = Array.isArray(value.steps) ? value.steps : [];

  return {
    projectId: typeof value.projectId === 'number' ? value.projectId : null,
    templateId: typeof value.templateId === 'number' ? value.templateId : null,
    hasRoute: value.hasRoute === true,
    steps,
    effectiveSource: typeof value.effectiveSource === 'string' ? value.effectiveSource : 'none',
    effectiveSteps: Array.isArray(value.effectiveSteps) ? value.effectiveSteps : steps,
    generalApproverAlsoApproves: value.generalApproverAlsoApproves === true,
    generalApproverRoleId: typeof value.generalApproverRoleId === 'number' ? value.generalApproverRoleId : null,
    canEdit: value.canEdit === true,
    workflowModeNone: value.workflowModeNone === true,
  };
}

/** Перелік у безпечній формі: не масив — порожньо; елементи — лише об'єкти. */
export function normalizeList<T>(raw: unknown): T[] {
  return Array.isArray(raw) ? (raw.filter((item) => typeof item === 'object' && item !== null) as T[]) : [];
}

/** Ролі для кроків. Під `['roles']`: зміна ролей на сторінці безпеки (інвалідація `['roles']`) оновлює і цей перелік. */
export const approverRolesKey = ['roles', 'approvers'] as const;

/** Шаблони проєкту з маршрутом, що діє. Під `['approval-route']`: збереження будь-якої області оновлює і їх. */
export function projectTemplateRoutesKey(projectId: number): readonly unknown[] {
  return ['approval-route', 'project-templates', projectId];
}

/**
 * Ключ каталогу для джерела маршруту (`effectiveSource`); невідоме значення — «маршруту немає», а не сирий рядок.
 * ⚠ Ключі — літералами: сторож `EndpointCoverageTests` (ключ змінною) мусить бачити, які рядки доходять до `t`.
 */
export function sourceLabel(source: string): string {
  switch (source) {
    case 'own':
      return t('approvalRoutes.source.own');
    case 'template':
      return t('approvalRoutes.source.template');
    case 'project':
      return t('approvalRoutes.source.project');
    case 'default':
      return t('approvalRoutes.source.default');
    default:
      return t('approvalRoutes.source.none');
  }
}

/** Назва ролі: локалізована назва, інакше код; позначка загального `Approver`. */
export function roleLabel(role: ApproverRoleDto): string {
  const name = localized(role.nameL10n);
  const base = name.length > 0 ? name : role.code;

  return role.isGeneralApprover ? t('approvalRoutes.generalApproverOption', { role: base }) : base;
}

/**
 * Назва ролі кроку за id. Ролі можуть не приїхати (читач із самим `Template.View` не має права на
 * `GET /roles/approvers`) — тоді «Роль #id», а не порожнє місце.
 */
export function roleNameOf(roles: readonly ApproverRoleDto[] | undefined, roleId: number): string {
  const role = roles?.find((candidate) => candidate.id === roleId);

  return role === undefined ? t('approvalRoutes.roleId', { id: roleId }) : roleLabel(role);
}

/**
 * Що перечитати після зміни маршруту: усі маршрути областей і шаблонів проєкту (`['approval-route']`, і маршрут
 * проєкту `ApprovalRouteEditor` теж під ним), черга погодження і повернуті (аркуші перезапущено з першого кроку),
 * зведення документів (стан кроку аркуша).
 */
export async function invalidateAfterRouteChange(client: QueryClient): Promise<void> {
  await client.invalidateQueries({ queryKey: ['approval-route'] });
  void client.invalidateQueries({ queryKey: ['approval-queue'] });
  void client.invalidateQueries({ queryKey: ['returned-sheets'] });
  void client.invalidateQueries({ queryKey: ['document'] });
}
