import { useState, type JSX } from 'react';
import { Badge, Button, Group, Modal, NumberInput, Select, Table, Text, TextInput } from '@mantine/core';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiEnqueue, apiFetch } from '@/api/client';
import type { BuildSnapshotRequest, PagedProjects, ReportSnapshotSummary } from '@/api/types';
import { can, useSession } from '@/shared/session/useSession';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { PageHeader } from '@/shared/ui/PageHeader';
import { showApiError, showDone } from '@/shared/ui/notify';
import { useUrlNumber } from '@/shared/ui/useUrlState';
import { t } from '@/shared/i18n';

/**
 * Зрізи регламентної звітності.
 *
 * ⛔ Це **не** екран звітів. Звітність лишається в SSRS (`D-52`), і маршрутів
 * `/reports/*` в інтерфейсі немає навмисно. Наша межа — `rpt.*`: незмінний
 * зріз, який SSRS читає. Тут його будують і бачать, на яких даних він
 * побудований.
 *
 * ⛔ Побудова не мала в клієнті жодного споживача (`A7-39`): зріз можна було
 * створити лише запитом повз інтерфейс, тобто звітність для регулятора
 * залежала від того, чи є поруч людина з `curl`.
 *
 * ⚠ Зріз **незмінний**: повторна побудова створює новий, а не переписує
 * старий. Інакше звіт, роздрукований учора, і той самий звіт сьогодні давали б
 * різні числа без жодного сліду (`ФВ-9.17`).
 */
export function SnapshotsPage(): JSX.Element {
  const queryClient = useQueryClient();
  const session = useSession();

  const [projectId, setProjectId] = useUrlNumber('projectId');
  const [periodKey, setPeriodKey] = useUrlNumber('periodKey');

  const [building, setBuilding] = useState(false);
  const [code, setCode] = useState('');
  const [buildPeriod, setBuildPeriod] = useState(currentPeriodKey());

  const projects = useQuery({
    queryKey: ['projects'],
    queryFn: () => apiFetch<PagedProjects>('/api/v1/projects?limit=200'),
  });

  const snapshots = useQuery({
    queryKey: ['snapshots', projectId, periodKey],
    queryFn: () =>
      apiFetch<ReportSnapshotSummary[]>(
        '/api/v1/reports/snapshots' +
          (projectId === null ? '' : `?projectId=${projectId}`) +
          (periodKey === null ? '' : `${projectId === null ? '?' : '&'}periodKey=${periodKey}`),
      ),
  });

  const build = useMutation({
    mutationFn: () =>
      apiEnqueue(`/api/v1/reports/${encodeURIComponent(code.trim())}/build`, {
        projectId: projectId ?? 0,
        periodKey: buildPeriod,
      } satisfies BuildSnapshotRequest),
    onSuccess: async (job) => {
      await queryClient.invalidateQueries({ queryKey: ['snapshots'] });
      setBuilding(false);
      showDone(t('snapshots.queued', { job: job.jobId }));
    },
    onError: showApiError,
  });

  return (
    <>
      <PageHeader
        title={t('snapshots.title')}
        actions={
          <Group gap="xs" align="end">
            <Select
              size="xs"
              miw={200}
              label={t('documents.project')}
              placeholder={t('periods.pickProject')}
              data={(projects.data?.items ?? []).map((project) => ({
                value: String(project.id),
                label: project.code,
              }))}
              value={projectId === null ? null : String(projectId)}
              onChange={(value) => setProjectId(value === null ? null : Number(value))}
            />

            <NumberInput
              size="xs"
              miw={110}
              label={t('documents.period')}
              value={periodKey ?? ''}
              onChange={(value) => setPeriodKey(typeof value === 'number' ? value : null)}
            />

            {can(session.data, 'Report.BuildSnapshot') && (
              <Button size="xs" disabled={projectId === null} onClick={() => setBuilding(true)}>
                {t('snapshots.build')}
              </Button>
            )}
          </Group>
        }
      />

      <AsyncBoundary<ReportSnapshotSummary[]>
        isPending={snapshots.isPending}
        error={snapshots.error}
        data={snapshots.data}
        isEmpty={(all) => all.length === 0}
        emptyTitle={t('snapshots.empty')}
        emptyHint={t('snapshots.emptyHint')}
        skeleton="table"
        onRetry={() => void snapshots.refetch()}
      >
        {(all) => (
          <Table striped className="ecr-sticky-head">
            <Table.Thead>
              <Table.Tr>
                <Table.Th>{t('snapshots.builtAt')}</Table.Th>
                <Table.Th>{t('documents.project')}</Table.Th>
                <Table.Th>{t('documents.period')}</Table.Th>
                <Table.Th>{t('snapshots.rows')}</Table.Th>
                <Table.Th>{t('snapshots.status')}</Table.Th>
                <Table.Th>{t('snapshots.hash')}</Table.Th>
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {all.map((snapshot) => (
                <Table.Tr key={snapshot.id}>
                  <Table.Td>
                    {snapshot.builtAt}
                    {snapshot.isCurrent && (
                      <Badge ml="xs" size="xs" variant="light">
                        {t('snapshots.current')}
                      </Badge>
                    )}
                  </Table.Td>
                  <Table.Td>{snapshot.projectId}</Table.Td>
                  <Table.Td>{snapshot.periodKey ?? '—'}</Table.Td>
                  <Table.Td>{snapshot.rowCount}</Table.Td>
                  <Table.Td>
                    {/* ⚠ Статус зрізу успадковується від стану даних: зріз
                        `Draft` існує, але регулятор його не бачить —
                        вʼюха `rpt.v_*` віддає лише `Approved` і `Submitted`
                        (`ФВ-10.11`). */}
                    <Badge variant="light">{snapshot.status}</Badge>
                  </Table.Td>
                  <Table.Td>
                    {/* ⛔ Контрольна сума показується цілком, а не обрізаною:
                        саме нею два зрізи одного періоду відрізняються один
                        від одного, і «перші вісім символів збіглися» — не
                        відповідь на питання «це той самий зріз». */}
                    <Text size="xs" style={{ wordBreak: 'break-all' }}>
                      {snapshot.contentHash ?? '—'}
                    </Text>
                  </Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        )}
      </AsyncBoundary>

      <Modal opened={building} onClose={() => setBuilding(false)} title={t('snapshots.build')}>
        <TextInput
          label={t('snapshots.code')}
          description={t('snapshots.codeHint')}
          value={code}
          onChange={(event) => setCode(event.currentTarget.value)}
          data-autofocus
        />

        <NumberInput
          mt="sm"
          label={t('documents.period')}
          value={buildPeriod}
          onChange={(value) => setBuildPeriod(typeof value === 'number' ? value : buildPeriod)}
        />

        <Text size="xs" c="dimmed" mt="sm">
          {t('snapshots.buildHint')}
        </Text>

        <Group justify="flex-end" mt="md">
          <Button variant="default" onClick={() => setBuilding(false)}>
            {t('common.cancel')}
          </Button>
          <Button
            disabled={code.trim().length === 0}
            loading={build.isPending}
            onClick={() => build.mutate()}
          >
            {t('snapshots.build')}
          </Button>
        </Group>
      </Modal>
    </>
  );
}

/**
 * Поточний період як `Year*100 + Sequence` (R-A6).
 *
 * ⚠ Лише початкове значення поля: справжній поточний період задає календар
 * проєкту, і в квартальному номер місяця йому не дорівнює.
 */
function currentPeriodKey(): number {
  const now = new Date();

  return now.getUTCFullYear() * 100 + (now.getUTCMonth() + 1);
}
