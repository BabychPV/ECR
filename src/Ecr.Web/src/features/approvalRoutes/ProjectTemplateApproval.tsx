import { useId, useState, type JSX, type ReactNode } from 'react';
import { Button, Group, Stack, Text, VisuallyHidden } from '@mantine/core';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type { ApprovalRouteDto } from '@/api/types';
import { ApprovalRouteEditor } from '@/features/projects/ApprovalRouteEditor';
import type { ProjectTemplateExtension } from '@/features/projects/ProjectTemplatesSection';
import { t } from '@/shared/i18n';
import { fetchApproverRoles, fetchProjectTemplateRoutes, type ApproverRoleDto, type ProjectTemplateRouteDto } from './approvalRoutesApi';
import { approverRolesKey, normalizeList, projectTemplateRoutesKey, roleNameOf, sourceLabel } from './approvalRouteModel';
import { ScopedApprovalRouteEditor } from './ScopedApprovalRouteEditor';

/** Кроки маршруту назвами ролей через «→»; порожньо — `null`. */
function stepsText(roles: readonly ApproverRoleDto[] | undefined, steps: ReadonlyArray<{ roleId: number }>): string | null {
  return steps.length === 0 ? null : steps.map((step) => roleNameOf(roles, step.roleId)).join(' → ');
}

/**
 * ✎ 24.11 (рішення людини «Одна таблиця шаблонів»): погодження на вкладці проєкту Templates — без другої таблиці.
 *
 * - `toolbar` — рядок «Маршрут погодження для всіх шаблонів: <кроки> [Edit approval route…]» (маршрут ПРОЄКТУ,
 *   `GET /projects/{id}/approval-route`, той самий ключ кешу, що й у діалозі `ApprovalRouteEditor`);
 * - `extension` — колонка «Approval» (джерело маршруту, що діє, і його кроки з `GET /projects/{id}/template-routes`),
 *   дія «Edit exception» і редактор винятку проєкту × шаблон під рядком (B155, варіант C).
 *
 * ⚠ Сервер не змінено: обидва запити — наявні; зіставлення з рядком таблиці шаблонів — за `templateId`. Шаблон,
 * якого ще немає в відповіді маршрутів (щойно приєднаний, перечитування в дорозі), показує порожню клітинку, а не
 * чужий маршрут.
 *
 * ⛔ Лише для `Project.Manage` + гранта Manage (як раніше розділи «Approval route» і «Templates: approval»):
 * `enabled=false` — нічого не читає й не повертає розширення.
 */
export function useProjectTemplateApproval(
  projectId: number,
  enabled: boolean,
): { readonly toolbar: ReactNode; readonly extension: ProjectTemplateExtension | undefined } {
  const queryClient = useQueryClient();
  const [open, setOpen] = useState<number | null>(null);
  const routeDescriptionId = useId();

  const routes = useQuery({
    queryKey: projectTemplateRoutesKey(projectId),
    queryFn: async () =>
      normalizeList<ProjectTemplateRouteDto>(await fetchProjectTemplateRoutes(projectId)).map((row) => ({
        ...row,
        effectiveSteps: Array.isArray(row.effectiveSteps) ? row.effectiveSteps : [],
      })),
    enabled,
  });
  const roles = useQuery({
    queryKey: approverRolesKey,
    queryFn: async () => normalizeList<ApproverRoleDto>(await fetchApproverRoles()),
    enabled,
  });
  // Той самий ключ і адреса, що в `ApprovalRouteEditor`: збереження там перечитує і цей рядок.
  const projectRoute = useQuery({
    queryKey: ['approval-route', projectId],
    queryFn: () => apiFetch<ApprovalRouteDto>(`/api/v1/projects/${String(projectId)}/approval-route`),
    enabled,
  });

  if (!enabled) return { toolbar: null, extension: undefined };

  const routeOf = (templateId: number): ProjectTemplateRouteDto | undefined =>
    routes.data?.find((candidate) => candidate.templateId === templateId);

  const rawSteps: unknown = projectRoute.data?.steps;
  const projectSteps = Array.isArray(rawSteps) ? (rawSteps as ApprovalRouteDto['steps']) : [];
  const toolbar = (
    <Group gap="sm" wrap="wrap" align="center" data-project-route="">
      <Text size="sm">
        {t('projects.templates.routeForAll')}{' '}
        <Text span fw={600} data-project-route-steps="">
          {projectRoute.isSuccess ? (stepsText(roles.data, projectSteps) ?? t('workflow.routeNone')) : '…'}
        </Text>
      </Text>
      {/* Опис дії — для читалки (`aria-describedby`), як у `SettingsAction`; на екрані рядок уже каже, що це маршрут. */}
      <VisuallyHidden id={routeDescriptionId}>{t('projectSettings.routeActionDesc')}</VisuallyHidden>
      <ApprovalRouteEditor
        projectId={projectId}
        openerLabel={t('projectSettings.routeAction')}
        describedBy={routeDescriptionId}
        // Маршрут проєкту — джерело «From the project» у колонці: після збереження перечитати і її.
        onSaved={() => void queryClient.invalidateQueries({ queryKey: projectTemplateRoutesKey(projectId) })}
      />
    </Group>
  );

  const extension: ProjectTemplateExtension = {
    header: t('projects.templates.approval'),
    cell: (row) => {
      const route = routeOf(row.templateId);
      if (route === undefined) return null;
      const steps = stepsText(roles.data, route.effectiveSteps);

      return (
        <Stack gap="xs" data-template-route-source={route.effectiveSource}>
          <Text size="sm" data-testid="project-template-route-source">
            {sourceLabel(route.effectiveSource)}
          </Text>
          {steps !== null && (
            <Text size="xs" c="dimmed">
              {steps}
            </Text>
          )}
          {route.generalApproverAlsoApproves && (
            <Text size="xs" c="dimmed">
              {t('approvalRoutes.generalApproverHint')}
            </Text>
          )}
          {route.workflowModeNone && (
            <Text size="xs" c="dimmed">
              {t('approvalRoutes.workflowModeNone')}
            </Text>
          )}
        </Stack>
      );
    },
    action: (row) => {
      const expanded = open === row.templateId;

      return (
        <Button
          size="xs"
          variant="default"
          aria-expanded={expanded}
          aria-label={t('approvalRoutes.editExceptionFor', { template: row.templateCode })}
          onClick={() => setOpen(expanded ? null : row.templateId)}
        >
          {t('approvalRoutes.editException')}
        </Button>
      );
    },
    expansion: (row): JSX.Element | null =>
      open === row.templateId ? (
        <ScopedApprovalRouteEditor
          scope={{ kind: 'projectTemplate', projectId, templateId: row.templateId }}
          onSaved={() => setOpen(null)}
        />
      ) : null,
  };

  return { toolbar, extension };
}
