import { useState, type JSX } from 'react';
import { Badge, Button, Group, Select, Table, Text } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { EcrApiError, apiFetch } from '@/api/client';
import type { PagedProjects, PeriodCalendarDto } from '@/api/types';
import { can, useSession } from '@/shared/session/useSession';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { PageHeader } from '@/shared/ui/PageHeader';
import { t } from '@/shared/i18n';

/**
 * Календар періодів проєкту.
 *
 * ⚠ Стан періоду **обчислюється з часу і зсувів**, а не зберігається полем:
 * збережений статус розійшовся б із календарем рівно тоді, коли фонова задача
 * не спрацювала. Тому екран показує те, що віддає сервер, і не рахує сам.
 *
 * ⚠ `PeriodKey = Year*100 + Sequence` (R-A6), і `Sequence` — це **порядковий
 * номер періоду**, а не місяць: у квартальному проєкті їх чотири.
 */
export function PeriodsPage(): JSX.Element {
  const [projectId, setProjectId] = useState<number | null>(null);
  const queryClient = useQueryClient();
  const session = useSession();

  // ⛔ Проєкти ВИБИРАЮТЬСЯ зі списку, а не вводяться номером. Це не про
  // зручність: без переліку не видно СТАНУ проєкту, а саме він визначає, чи
  // відкриються періоди взагалі (`A7-25`).
  const projects = useQuery({
    queryKey: ['projects'],
    queryFn: () => apiFetch<PagedProjects>('/api/v1/projects?limit=200'),
  });

  const periods = useQuery({
    queryKey: ['periods', projectId],
    queryFn: () => apiFetch<PeriodCalendarDto>(`/api/v1/projects/${projectId ?? 0}/periods`),
    enabled: projectId !== null,
  });

  const selected = (projects.data?.items ?? []).find((p) => p.id === projectId);

  const activate = useMutation({
    mutationFn: (id: number) => apiFetch(`/api/v1/projects/${id}/activate`, { method: 'POST' }),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['projects'] });
      await queryClient.invalidateQueries({ queryKey: ['periods', projectId] });
      notifications.show({ color: 'green', message: t('periods.activated') });
    },
    onError: (error) => {
      notifications.show({
        color: 'red',
        message: error instanceof EcrApiError ? error.message : String(error),
      });
    },
  });

  return (
    <>
      <PageHeader
        title={t('periods.title')}
        actions={
          <Group gap="xs">
            <Select
              size="xs"
              miw={220}
              placeholder={t('periods.pickProject')}
              data={(projects.data?.items ?? []).map((p) => ({
                value: String(p.id),
                label: `${p.code} · ${p.status}`,
              }))}
              value={projectId === null ? null : String(projectId)}
              onChange={(value) => setProjectId(value === null ? null : Number(value))}
            />

            {/* ⛔ Кнопка є лише для чернетки. Доки проєкт не активований,
                задача станів до нього не доходить, періоди лишаються
                `Scheduled`, і система відмовляє в кожній комірці з причиною
                «період ще не відкрито» — неправдивою (`A7-25`). */}
            {selected?.status === 'Draft' && can(session.data, 'Project.Manage') && (
              <Button
                size="xs"
                loading={activate.isPending}
                onClick={() => activate.mutate(selected.id)}
              >
                {t('periods.activate')}
              </Button>
            )}
          </Group>
        }
      />

      {/* Недоступний перелік проєктів лишає порожнім сам вибір — це треба
          сказати, а не показати порожній Select. */}
      <AsyncBoundary<PagedProjects>
        isPending={projects.isPending}
        error={projects.error}
        data={projects.data}
        isEmpty={(page) => page.items.length === 0}
        emptyTitle={t('periods.noProjects')}
        emptyHint={t('periods.noProjectsHint')}
        onRetry={() => void projects.refetch()}
      >
        {() => null}
      </AsyncBoundary>

      {selected?.status === 'Draft' && (
        <Text c="orange" size="sm" mb="xs">
          {t('periods.draftHint')}
        </Text>
      )}

      {/*
       * ⚠ Доки проєкт не обрано, `data` — `undefined`: запиту ще не було, і
       * обгортка каже саме це, а не «періодів немає».
       */}
      <AsyncBoundary<PeriodCalendarDto>
        isPending={projectId !== null && periods.isPending}
        error={periods.error}
        data={projectId === null ? undefined : periods.data}
        isEmpty={(calendar) => calendar.periods.length === 0}
        emptyTitle={projectId === null ? t('periods.pickProject') : t('periods.noPeriods')}
        emptyHint={projectId === null ? undefined : t('periods.noPeriodsHint')}
        skeleton="table"
        onRetry={() => void periods.refetch()}
      >
        {(calendar) => (
        <Table striped className="ecr-sticky-head">
          <Table.Thead>
            <Table.Tr>
              <Table.Th>{t('periods.key')}</Table.Th>
              <Table.Th>{t('periods.sequence')}</Table.Th>
              <Table.Th>{t('periods.range')}</Table.Th>
              <Table.Th>{t('periods.state')}</Table.Th>
              <Table.Th>{t('periods.grace')}</Table.Th>
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {calendar.periods.map((period) => (
              <Table.Tr key={period.periodKey}>
                <Table.Td>{period.periodKey}</Table.Td>
                <Table.Td>{period.sequence}</Table.Td>
                <Table.Td>
                  {period.startsAt} — {period.endsAt}
                </Table.Td>
                <Table.Td>
                  <Badge color={stateColor(period.state)} variant="light">
                    {period.state}
                  </Badge>
                </Table.Td>
                <Table.Td>{period.graceEndsAt ?? '—'}</Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
        )}
      </AsyncBoundary>
    </>
  );
}

/**
 * Колір стану.
 *
 * ⚠ `Grace` виділено окремим кольором, а не зведено до «відкритого»: правка в
 * пільговому строку позначається як пізня (D-70) і виглядає в аудиті інакше.
 */
function stateColor(state: string): string {
  switch (state) {
    case 'Open':
      return 'green';
    case 'Grace':
      return 'yellow';
    case 'Closed':
      return 'gray';
    default:
      return 'blue';
  }
}
