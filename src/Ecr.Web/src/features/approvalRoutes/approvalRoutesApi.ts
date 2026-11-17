import { apiFetch } from '@/api/client';
import type { components } from '@/api/schema';

/**
 * ✎ B155 (варіант C, 16.11): маршрути погодження ШАБЛОНУ (усі проєкти), виняток проєкту для шаблону, типовий маршрут
 * майданчика, ролі для кроків і шаблони проєкту з маршрутом, що діє.
 *
 * ⚠ Лише адреси й методи — екрани (вкладка «Погодження» шаблону, блок шаблонів у налаштуваннях проєкту, діалог із
 * попередженням «N поданих почнуть з першого кроку») — окрема клієнтська порція.
 *
 * ⛔ Крок маршруту шаблону затверджує роль кроку АБО вбудований загальний `Approver`
 * (`generalApproverAlsoApproves`). Зміна маршруту повертає подані аркуші на перший крок: спершу `preview…`, потім `PUT`.
 */
type Schemas = components['schemas'];

/** Маршрут області з тим, що в ній діє. */
export type ScopedApprovalRouteDto = Schemas['ScopedApprovalRouteDto'];

/** Подані аркуші, які зміна поверне на перший крок. */
export type ApprovalRouteImpactDto = Schemas['ApprovalRouteImpactDto'];

/** Відповідь заміни маршруту. */
export type ApprovalRouteChangeDto = Schemas['ApprovalRouteChangeDto'];

/** Шаблон проєкту з маршрутом, що діє. */
export type ProjectTemplateRouteDto = Schemas['ProjectTemplateRouteDto'];

/** Роль для кроку маршруту. */
export type ApproverRoleDto = Schemas['ApproverRoleDto'];

/** Область маршруту: шаблон в усіх проєктах, виняток проєкту для шаблону або типовий майданчика. */
export type ApprovalRouteScope =
  | { kind: 'template'; templateId: number }
  | { kind: 'projectTemplate'; projectId: number; templateId: number }
  | { kind: 'default' };

/** Маршрут області. */
export function fetchScopedApprovalRoute(scope: ApprovalRouteScope): Promise<ScopedApprovalRouteDto> {
  switch (scope.kind) {
    case 'template':
      return apiFetch<ScopedApprovalRouteDto>(`/api/v1/templates/${String(scope.templateId)}/approval-route`);
    case 'projectTemplate':
      return apiFetch<ScopedApprovalRouteDto>(
        `/api/v1/projects/${String(scope.projectId)}/templates/${String(scope.templateId)}/approval-route`,
      );
    case 'default':
      return apiFetch<ScopedApprovalRouteDto>('/api/v1/approval-route/default');
  }
}

/** Замінює маршрут області; порожній набір прибирає власний маршрут (діє успадкований). */
export function replaceScopedApprovalRoute(
  scope: ApprovalRouteScope,
  roleIds: number[],
): Promise<ApprovalRouteChangeDto> {
  switch (scope.kind) {
    case 'template':
      return apiFetch<ApprovalRouteChangeDto>(`/api/v1/templates/${String(scope.templateId)}/approval-route`, {
        method: 'PUT',
        body: JSON.stringify({ roleIds }),
      });
    case 'projectTemplate':
      return apiFetch<ApprovalRouteChangeDto>(
        `/api/v1/projects/${String(scope.projectId)}/templates/${String(scope.templateId)}/approval-route`,
        { method: 'PUT', body: JSON.stringify({ roleIds }) },
      );
    case 'default':
      return apiFetch<ApprovalRouteChangeDto>('/api/v1/approval-route/default', {
        method: 'PUT',
        body: JSON.stringify({ roleIds }),
      });
  }
}

/**
 * Скільки поданих аркушів зміна поверне на перший крок (до збереження). `roleIds` — запропонований набір; без нього —
 * будь-яка зміна. Набір, рівний наявному, — 0.
 */
export function previewScopedApprovalRoute(
  scope: ApprovalRouteScope,
  roleIds?: number[],
): Promise<ApprovalRouteImpactDto> {
  switch (scope.kind) {
    case 'template':
      return apiFetch<ApprovalRouteImpactDto>(`/api/v1/templates/${String(scope.templateId)}/approval-route/preview`, {
        method: 'POST',
        body: JSON.stringify({ roleIds: roleIds ?? null }),
      });
    case 'projectTemplate':
      return apiFetch<ApprovalRouteImpactDto>(
        `/api/v1/projects/${String(scope.projectId)}/templates/${String(scope.templateId)}/approval-route/preview`,
        { method: 'POST', body: JSON.stringify({ roleIds: roleIds ?? null }) },
      );
    case 'default':
      return apiFetch<ApprovalRouteImpactDto>('/api/v1/approval-route/default/preview', {
        method: 'POST',
        body: JSON.stringify({ roleIds: roleIds ?? null }),
      });
  }
}

/** Шаблони проєкту (основний і приєднані) з маршрутом, що діє для кожного. */
export function fetchProjectTemplateRoutes(projectId: number): Promise<ProjectTemplateRouteDto[]> {
  return apiFetch<ProjectTemplateRouteDto[]>(`/api/v1/projects/${String(projectId)}/template-routes`);
}

/** Ролі для кроків маршруту — і для тих, хто не має `Security.ManageRoles`. */
export function fetchApproverRoles(): Promise<ApproverRoleDto[]> {
  return apiFetch<ApproverRoleDto[]>('/api/v1/roles/approvers');
}

/** Ключ кешу маршруту області (для інвалідації після збереження). */
export function approvalRouteKey(scope: ApprovalRouteScope): readonly unknown[] {
  switch (scope.kind) {
    case 'template':
      return ['approval-route', 'template', scope.templateId];
    case 'projectTemplate':
      return ['approval-route', 'project-template', scope.projectId, scope.templateId];
    case 'default':
      return ['approval-route', 'default'];
  }
}
