import { useId, useRef, useState, type JSX } from 'react';
import { Alert, Badge, Button, Group, List, Select, Stack, Text } from '@mantine/core';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { t } from '@/shared/i18n';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { showApiError, showDone } from '@/shared/ui/notify';
import {
  approvalRouteKey,
  fetchApproverRoles,
  fetchScopedApprovalRoute,
  previewScopedApprovalRoute,
  replaceScopedApprovalRoute,
  type ApprovalRouteImpactDto,
  type ApprovalRouteScope,
  type ApproverRoleDto,
  type ScopedApprovalRouteDto,
} from './approvalRoutesApi';
import {
  MaxApprovalSteps,
  appendProblem,
  approverRolesKey,
  invalidateAfterRouteChange,
  normalizeList,
  normalizeRoute,
  roleLabel,
  roleNameOf,
  sourceLabel,
  stepsProblem,
  type StepsProblem,
} from './approvalRouteModel';
import { RestartConfirm } from './RestartConfirm';

/**
 * ✎ B155 (варіант C): маршрут погодження ОБЛАСТІ — шаблон в усіх проєктах, виняток проєкту для шаблону або
 * типовий маршрут майданчика.
 *
 * Показує власні кроки або успадковані (`effectiveSteps`) із позначкою джерела; підказку, що загальний `Approver`
 * теж затверджує кожен крок маршруту шаблону; «лише читання» без `canEdit`; і «маршрут не діє», коли найновіша
 * версія шаблону без подання й затвердження (`workflowModeNone`, B75).
 *
 * ⛔ Збереження — у два кроки: спершу `preview` (скільки поданих аркушів зміна поверне на перший крок), і лише
 * потім `PUT`. Є що перезапускати — `PUT` іде тільки після підтвердження людини.
 *
 * ⛔ Редагування відкривається лише з ЗАВАНТАЖЕНОГО маршруту: `PUT` — повна заміна (порожній набір прибирає власний
 * маршрут), і чернетка, засіяна з невдалого запиту, мовчки стерла б наявний маршрут (те саме, що
 * `ApprovalRouteEditor.tsx`).
 */
export function ScopedApprovalRouteEditor({
  scope,
  onSaved,
}: {
  readonly scope: ApprovalRouteScope;
  /** Після успішного збереження (наприклад, згорнути рядок у налаштуваннях проєкту). */
  readonly onSaved?: (() => void) | undefined;
}): JSX.Element {
  const queryClient = useQueryClient();
  const problemId = useId();

  const route = useQuery({
    queryKey: approvalRouteKey(scope),
    queryFn: async () => normalizeRoute(await fetchScopedApprovalRoute(scope)),
  });

  // ⚠ Читач із самим `Template.View` прав на ролі не має: відмова тут не ламає показу (назви — «Роль #id»), лише
  // редагування, яке йому однаково недоступне.
  const roles = useQuery({
    queryKey: approverRolesKey,
    queryFn: async () => normalizeList<ApproverRoleDto>(await fetchApproverRoles()),
  });

  const [editing, setEditing] = useState(false);
  const [draft, setDraft] = useState<number[]>([]);
  const [addProblem, setAddProblem] = useState<StepsProblem>(null);
  const [confirm, setConfirm] = useState<{ roleIds: number[]; impact: ApprovalRouteImpactDto } | null>(null);

  /*
   * ⛔ B145b: другий клік, поки попередній запит у дорозі, не шле другого `preview`/`PUT`. `isPending` мутації
   * оновлюється лише з наступним рендером — ref закриває і клік у той самий такт.
   */
  const inFlight = useRef(false);

  const replace = useMutation({
    mutationFn: (roleIds: number[]) => replaceScopedApprovalRoute(scope, roleIds),
    onSuccess: async (result) => {
      setConfirm(null);
      setEditing(false);
      showDone(
        result.steps === 0
          ? t('approvalRoutes.removed', { restarted: result.restarted.total })
          : t('approvalRoutes.saved', { steps: result.steps, restarted: result.restarted.total }),
      );
      await invalidateAfterRouteChange(queryClient);
      onSaved?.();
    },
    // ⚠ Відмова (`422 ECR-DOC-0422`, `404 ECR-SEC-0404`, `403`…) — текстом сервера; чернетка лишається для виправлення.
    onError: (error) => {
      setConfirm(null);
      showApiError(error);
    },
    onSettled: () => {
      inFlight.current = false;
    },
  });

  const preview = useMutation({
    mutationFn: (roleIds: number[]) => previewScopedApprovalRoute(scope, roleIds),
    onSuccess: (impact, roleIds) => {
      if (impact.total > 0) {
        // Рішення за людиною: запит відпущено, `PUT` — лише з підтвердження.
        inFlight.current = false;
        setConfirm({ roleIds, impact });
        return;
      }

      replace.mutate(roleIds);
    },
    onError: (error) => {
      inFlight.current = false;
      showApiError(error);
    },
  });

  const busy = preview.isPending || replace.isPending;

  function submit(roleIds: number[]): void {
    // ⛔ Лише з відкритого редагування над підтвердженим маршрутом і без запиту в дорозі.
    if (!editing || inFlight.current || busy || route.data === undefined || route.error !== null) return;
    if (stepsProblem(roleIds) !== null) return;

    inFlight.current = true;
    preview.mutate(roleIds);
  }

  function confirmRestart(): void {
    if (confirm === null || inFlight.current || replace.isPending) return;

    inFlight.current = true;
    replace.mutate(confirm.roleIds);
  }

  function startEditing(loaded: ScopedApprovalRouteDto): void {
    setDraft(loaded.steps.map((step) => step.roleId));
    setAddProblem(null);
    setEditing(true);
  }

  function addStep(value: string | null): void {
    if (value === null) return;

    const roleId = Number(value);
    const problem = appendProblem(draft, roleId);
    setAddProblem(problem);
    if (problem === null) setDraft([...draft, roleId]);
  }

  const options = (roles.data ?? []).map((role) => ({ value: String(role.id), label: roleLabel(role) }));

  return (
    <>
      <AsyncBoundary<ScopedApprovalRouteDto>
        isPending={route.isPending}
        error={route.error}
        data={route.data}
        skeleton="form"
        onRetry={() => void route.refetch()}
      >
        {(loaded) => {
          const draftProblem = stepsProblem(draft);
          const shownProblem = addProblem ?? draftProblem;
          const ownIds = loaded.steps.map((step) => step.roleId);
          const unchanged = draft.length === ownIds.length && draft.every((roleId, index) => roleId === ownIds[index]);

          return (
            <Stack gap="xs" data-testid="scoped-approval-route" data-source={loaded.effectiveSource}>
              <Group gap="xs">
                <Badge variant="light" data-testid="approval-route-source">
                  {sourceLabel(loaded.effectiveSource)}
                </Badge>
              </Group>

              {loaded.workflowModeNone && (
                <Alert color="statusWarning" data-testid="approval-route-mode-none">
                  {t('approvalRoutes.workflowModeNone')}
                </Alert>
              )}

              {loaded.generalApproverAlsoApproves && (
                <Text size="sm" data-testid="approval-route-general-approver">
                  {t('approvalRoutes.generalApproverHint')}
                </Text>
              )}

              {!loaded.hasRoute && loaded.effectiveSource !== 'none' && (
                <Text size="sm" c="dimmed">
                  {t('approvalRoutes.inheritedHint')}
                </Text>
              )}

              {loaded.effectiveSteps.length === 0 ? (
                <Text size="sm" data-testid="approval-route-no-steps">
                  {t('workflow.routeNone')}
                </Text>
              ) : (
                <List type="ordered" size="sm" aria-label={t('approvalRoutes.stepsLabel')} data-testid="approval-route-steps">
                  {loaded.effectiveSteps.map((step) => (
                    <List.Item key={`${String(step.ordinal)}:${String(step.roleId)}`}>
                      {roleNameOf(roles.data, step.roleId)}
                    </List.Item>
                  ))}
                </List>
              )}

              {!loaded.canEdit && (
                <Text size="xs" c="dimmed" data-testid="approval-route-read-only">
                  {t('approvalRoutes.readOnly')}
                </Text>
              )}

              {loaded.canEdit && !editing && (
                <Group>
                  <Button variant="default" disabled={loaded.workflowModeNone} onClick={() => startEditing(loaded)}>
                    {t('approvalRoutes.edit')}
                  </Button>
                </Group>
              )}

              {loaded.canEdit && editing && (
                <Stack gap="xs" data-testid="approval-route-editor">
                  {draft.length === 0 ? (
                    <Text size="sm" c="dimmed">
                      {t('approvalRoutes.draftEmpty')}
                    </Text>
                  ) : (
                    <Stack gap="xs" aria-label={t('approvalRoutes.draftLabel')} role="list" data-testid="approval-route-draft">
                      {draft.map((roleId, index) => (
                        // Порядок кроків і є маршрутом, тож ключ — позиція: та сама роль законно стоїть на різних кроках.
                        <Group key={`${String(index)}:${String(roleId)}`} gap="xs" role="listitem">
                          <Badge variant="light">{index + 1}</Badge>
                          <Text size="sm" style={{ flex: 1 }}>
                            {roleNameOf(roles.data, roleId)}
                          </Text>
                          <Button
                            size="xs"
                            variant="subtle"
                            color="statusError"
                            onClick={() => {
                              setAddProblem(null);
                              setDraft(draft.filter((_, i) => i !== index));
                            }}
                          >
                            {t('approvalRoutes.removeStep', { step: index + 1 })}
                          </Button>
                        </Group>
                      ))}
                    </Stack>
                  )}

                  <AsyncBoundary
                    isPending={roles.isPending}
                    error={roles.error}
                    data={roles.data}
                    skeleton="form"
                    onRetry={() => void roles.refetch()}
                  >
                    {() => (
                      <Select
                        label={t('workflow.routeAddStep')}
                        description={t('workflow.routeAddStepHint')}
                        data={options}
                        value={null}
                        onChange={addStep}
                        disabled={draft.length >= MaxApprovalSteps}
                        searchable
                        {...(shownProblem === null ? {} : { 'aria-describedby': problemId })}
                      />
                    )}
                  </AsyncBoundary>

                  {draft.length >= MaxApprovalSteps && shownProblem === null && (
                    <Text size="xs" c="dimmed">
                      {t('approvalRoutes.tooLong', { max: MaxApprovalSteps })}
                    </Text>
                  )}

                  {shownProblem !== null && (
                    <Text size="sm" c="statusError" id={problemId} role="alert" data-testid="approval-route-problem">
                      {shownProblem === 'tooLong'
                        ? t('approvalRoutes.tooLong', { max: MaxApprovalSteps })
                        : t('approvalRoutes.consecutiveRole')}
                    </Text>
                  )}

                  <Group justify="flex-end" gap="xs">
                    <Button variant="default" onClick={() => setEditing(false)} disabled={busy}>
                      {t('common.cancel')}
                    </Button>
                    {loaded.hasRoute && (
                      <Button
                        variant="outline"
                        color="statusError"
                        disabled={busy}
                        onClick={() => submit([])}
                        data-testid="approval-route-remove-own"
                      >
                        {t('approvalRoutes.removeOwn')}
                      </Button>
                    )}
                    {/* ⛔ Заблоковано, доки маршрут не підтверджено (див. шапку), набір порушує правила або не змінився. */}
                    <Button
                      loading={busy}
                      disabled={route.isPending || route.error !== null || draftProblem !== null || unchanged}
                      onClick={() => submit(draft)}
                      data-testid="approval-route-save"
                    >
                      {t('common.save')}
                    </Button>
                  </Group>
                </Stack>
              )}
            </Stack>
          );
        }}
      </AsyncBoundary>

      <RestartConfirm
        impact={confirm?.impact ?? null}
        isPending={replace.isPending}
        onConfirm={confirmRestart}
        onClose={() => {
          if (!replace.isPending) setConfirm(null);
        }}
      />
    </>
  );
}
