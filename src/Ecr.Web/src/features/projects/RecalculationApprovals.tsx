import { useState, type JSX } from 'react';
import { Button, Group, Stack, Table, Title } from '@mantine/core';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiEnqueue, apiFetch } from '@/api/client';
import type { components } from '@/api/schema';
import { hasProjectGrant } from '@/features/documents/BusinessKeyChangeAction';
import { humanizeJobId } from '@/features/workflow/jobLabel';
import { formatDateTime } from '@/shared/format';
import { t } from '@/shared/i18n';
import { useSession } from '@/shared/session/useSession';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { ReasonModal } from '@/shared/ui/ReasonModal';
import { showApiError, showDone } from '@/shared/ui/notify';

type Approval = components['schemas']['RecalculationApprovalDto'];
type ApprovalRequest = components['schemas']['RecalculationApprovalRequest'];
type RecalculationRequest = components['schemas']['ProjectRecalculationRequest'];

/**
 * Перерахунок закритого періоду — «чотири ока» (ФВ-9.7, аудит безпеки S1).
 *
 * ⛔ Друга людина підтверджує СВОЄЮ сесією: екран не шле нічийого
 * ідентифікатора, лише `approvalId`. Кнопки «Підтвердити» на власному запиті
 * немає — сервер відмовив би `ECR-CALC-0409`, і обіцяти таку дію нечесно.
 */
const approvalsKey = (projectId: number): readonly unknown[] => ['recalcApprovals', projectId];

const approvalsUrl = (projectId: number): string => `/api/v1/projects/${String(projectId)}/recalculation-approvals`;

/** Кнопка на рядку закритого періоду: запит погодження з причиною. */
export function RequestRecalculationButton({
  projectId,
  periodKey,
}: {
  projectId: number;
  periodKey: number;
}): JSX.Element {
  const queryClient = useQueryClient();
  const [opened, setOpened] = useState(false);

  const request = useMutation({
    mutationFn: (reason: string) =>
      apiFetch<Approval>(approvalsUrl(projectId), {
        method: 'POST',
        body: JSON.stringify({ periodKey, reason } satisfies ApprovalRequest),
      }),
    onSuccess: async () => {
      setOpened(false);
      showDone(t('recalcApprovals.requested'));
      await queryClient.invalidateQueries({ queryKey: approvalsKey(projectId) });
    },
    onError: showApiError,
  });

  return (
    <>
      <Button size="compact-xs" variant="subtle" onClick={() => setOpened(true)}>
        {t('recalcApprovals.request')}
      </Button>
      <ReasonModal
        opened={opened}
        title={t('recalcApprovals.requestTitle', { period: String(periodKey) })}
        label={t('recalcApprovals.reason')}
        description={t('recalcApprovals.requestHint')}
        confirmLabel={t('recalcApprovals.request')}
        isPending={request.isPending}
        onConfirm={(reason) => request.mutate(reason)}
        onClose={() => setOpened(false)}
      />
    </>
  );
}

/** Живі погодження проєкту: підтвердження чужих і запуск власних підтверджених. */
export function RecalculationApprovalsPanel({ projectId }: { projectId: number }): JSX.Element | null {
  const queryClient = useQueryClient();
  const session = useSession();
  const me = session.data?.userId ?? null;
  const manages = hasProjectGrant(session.data ?? undefined, projectId, 'Manage');

  const approvals = useQuery({
    queryKey: approvalsKey(projectId),
    queryFn: () => apiFetch<Approval[]>(`/api/v1/projects/${String(projectId)}/recalculation-approvals`),
  });

  const refresh = (): Promise<void> => queryClient.invalidateQueries({ queryKey: approvalsKey(projectId) });

  const confirm = useMutation({
    mutationFn: (id: number) =>
      apiFetch<Approval>(`/api/v1/projects/${String(projectId)}/recalculation-approvals/${String(id)}/confirm`, {
        method: 'POST',
      }),
    onSuccess: async () => {
      showDone(t('recalcApprovals.confirmedDone'));
      await refresh();
    },
    onError: showApiError,
  });

  const run = useMutation({
    mutationFn: (approval: Approval) =>
      apiEnqueue(`/api/v1/projects/${String(projectId)}/recalculate`, {
        periodKey: approval.periodKey,
        approvalId: approval.id,
      } satisfies RecalculationRequest),
    onSuccess: async (job) => {
      showDone(t('workflow.recalcQueued', { job: humanizeJobId(job.jobId) }));
      await refresh();
    },
    onError: showApiError,
  });

  return (
    <Stack gap="xs" mt="md">
      <Title order={3}>{t('recalcApprovals.title')}</Title>
      {/* ⛔ Була `Array.isArray(data) ? data : []`: відмова сервера і запит у
          дорозі показували «погоджень немає», і друга людина не бачила, що
          на неї чекають (`ФВ-14.22`). Межа станів розрізняє всі чотири. */}
      <AsyncBoundary<Approval[]>
        isPending={approvals.isPending}
        error={approvals.error}
        data={approvals.data}
        isEmpty={(rows) => rows.length === 0}
        emptyTitle={t('recalcApprovals.empty')}
        onRetry={() => void approvals.refetch()}
      >
        {(rows) => (
            <Table striped>
              <Table.Thead>
                <Table.Tr>
                  <Table.Th>{t('recalcApprovals.period')}</Table.Th>
                  <Table.Th>{t('recalcApprovals.requestedBy')}</Table.Th>
                  <Table.Th>{t('recalcApprovals.reason')}</Table.Th>
                  <Table.Th>{t('recalcApprovals.state')}</Table.Th>
                  <Table.Th>{t('recalcApprovals.expires')}</Table.Th>
                  <Table.Th>{t('common.actions')}</Table.Th>
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {rows.map((a) => {
                  const confirmed = a.confirmedByUserId !== null;
                  const mine = me !== null && a.requestedByUserId === me;

                  return (
                    <Table.Tr key={a.id}>
                      <Table.Td>{a.periodKey}</Table.Td>
                      <Table.Td>{a.requestedByName ?? `#${String(a.requestedByUserId)}`}</Table.Td>
                      <Table.Td>{a.reason}</Table.Td>
                      <Table.Td>
                        {confirmed
                          ? t('recalcApprovals.stateConfirmed', {
                              name: a.confirmedByName ?? `#${String(a.confirmedByUserId)}`,
                            })
                          : t('recalcApprovals.statePending')}
                      </Table.Td>
                      <Table.Td>{formatDateTime(a.expiresAt)}</Table.Td>
                      <Table.Td>
                        <Group gap="xs" justify="flex-end">
                          {!confirmed && !mine && manages && (
                            <Button
                              size="compact-xs"
                              variant="subtle"
                              loading={confirm.isPending}
                              onClick={() => confirm.mutate(a.id)}
                            >
                              {t('recalcApprovals.confirm')}
                            </Button>
                          )}
                          {confirmed && mine && (
                            <Button
                              size="compact-xs"
                              variant="subtle"
                              loading={run.isPending}
                              onClick={() => run.mutate(a)}
                            >
                              {t('workflow.recalculate')}
                            </Button>
                          )}
                        </Group>
                      </Table.Td>
                    </Table.Tr>
                  );
                })}
              </Table.Tbody>
            </Table>
        )}
      </AsyncBoundary>
    </Stack>
  );
}
