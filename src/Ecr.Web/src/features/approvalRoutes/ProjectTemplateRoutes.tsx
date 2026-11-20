import { Fragment, useState, type JSX } from 'react';
import { Badge, Button, Stack, Table, Text, VisuallyHidden } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import { localized } from '@/shared/i18n/localized';
import { t } from '@/shared/i18n';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { CodeText } from '@/shared/ui/CodeText';
import {
  fetchApproverRoles,
  fetchProjectTemplateRoutes,
  type ApproverRoleDto,
  type ProjectTemplateRouteDto,
} from './approvalRoutesApi';
import { approverRolesKey, normalizeList, projectTemplateRoutesKey, roleNameOf, sourceLabel } from './approvalRouteModel';
import { ScopedApprovalRouteEditor } from './ScopedApprovalRouteEditor';

/**
 * ✎ B155 (варіант C): налаштування проєкту → «Шаблони — погодження». Рядок на шаблон проєкту (основний і
 * приєднані): код, назва, джерело маршруту, що діє, його кроки, позначка «загальний Approver теж затверджує».
 *
 * Виняток проєкту для шаблону (`{kind:'projectTemplate'}`) редагується в рядку: «Змінити виняток» розгортає
 * редактор області під рядком. Прибрати виняток — порожній набір (діє маршрут шаблону).
 *
 * ⚠ Маршрут самого проєкту (`/projects/{id}/approval-route`) лишається своїм редактором (`ApprovalRouteEditor`).
 */
export function ProjectTemplateRoutes({ projectId }: { readonly projectId: number }): JSX.Element {
  const routes = useQuery({
    queryKey: projectTemplateRoutesKey(projectId),
    queryFn: async () =>
      normalizeList<ProjectTemplateRouteDto>(await fetchProjectTemplateRoutes(projectId)).map((row) => ({
        ...row,
        effectiveSteps: Array.isArray(row.effectiveSteps) ? row.effectiveSteps : [],
      })),
  });
  const roles = useQuery({
    queryKey: approverRolesKey,
    queryFn: async () => normalizeList<ApproverRoleDto>(await fetchApproverRoles()),
  });

  const [open, setOpen] = useState<number | null>(null);

  return (
    <AsyncBoundary<ProjectTemplateRouteDto[]>
      isPending={routes.isPending}
      error={routes.error}
      data={routes.data}
      isEmpty={(all) => all.length === 0}
      emptyTitle={t('approvalRoutes.noTemplates')}
      skeleton="table"
      onRetry={() => void routes.refetch()}
    >
      {(all) => (
        <Table.ScrollContainer minWidth={420} data-testid="project-template-routes-scroll">
        <Table data-testid="project-template-routes" verticalSpacing="xs">
          <Table.Thead>
            <Table.Tr>
              <Table.Th scope="col">{t('approvalRoutes.colTemplate')}</Table.Th>
              <Table.Th scope="col">{t('approvalRoutes.colSource')}</Table.Th>
              <Table.Th scope="col">{t('approvalRoutes.colSteps')}</Table.Th>
              <Table.Th scope="col">
                <VisuallyHidden>{t('approvalRoutes.colActions')}</VisuallyHidden>
              </Table.Th>
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {all.map((row) => {
              const name = localized(row.nameL10n);
              const expanded = open === row.templateId;

              return (
                <Fragment key={row.templateId}>
                <Table.Tr data-template-id={row.templateId} data-source={row.effectiveSource}>
                  <Table.Td>
                    <Stack gap="xs">
                      <CodeText>{row.code}</CodeText>
                      {name.length > 0 && (
                        <Text size="xs" c="dimmed">
                          {name}
                        </Text>
                      )}
                    </Stack>
                  </Table.Td>
                  <Table.Td>
                    <Badge variant="light" data-testid="project-template-route-source">
                      {sourceLabel(row.effectiveSource)}
                    </Badge>
                  </Table.Td>
                  <Table.Td>
                    <Stack gap="xs">
                      <Text size="sm">
                        {row.effectiveSteps.length === 0
                          ? t('workflow.routeNone')
                          : row.effectiveSteps.map((step) => roleNameOf(roles.data, step.roleId)).join(' → ')}
                      </Text>
                      {row.generalApproverAlsoApproves && (
                        <Text size="xs" c="dimmed">
                          {t('approvalRoutes.generalApproverHint')}
                        </Text>
                      )}
                      {row.workflowModeNone && (
                        <Text size="xs" c="dimmed">
                          {t('approvalRoutes.workflowModeNone')}
                        </Text>
                      )}
                    </Stack>
                  </Table.Td>
                  <Table.Td>
                    <Button
                      size="xs"
                      variant="default"
                      aria-expanded={expanded}
                      aria-label={t('approvalRoutes.editExceptionFor', { template: row.code })}
                      onClick={() => setOpen(expanded ? null : row.templateId)}
                    >
                      {t('approvalRoutes.editException')}
                    </Button>
                  </Table.Td>
                </Table.Tr>
                {expanded ? (
                  <Table.Tr>
                    <Table.Td colSpan={4}>
                      <ScopedApprovalRouteEditor
                        scope={{ kind: 'projectTemplate', projectId, templateId: row.templateId }}
                        onSaved={() => setOpen(null)}
                      />
                    </Table.Td>
                  </Table.Tr>
                ) : null}
                </Fragment>
              );
            })}
          </Table.Tbody>
        </Table>
        </Table.ScrollContainer>
      )}
    </AsyncBoundary>
  );
}
