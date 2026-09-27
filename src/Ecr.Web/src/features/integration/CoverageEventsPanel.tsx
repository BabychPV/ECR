import type { JSX } from 'react';
import { Button, Group, Stack, Text, Title } from '@mantine/core';
import { useInfiniteQuery } from '@tanstack/react-query';
import {
  CoverageEventStatuses,
  coverageEventsQueryKey,
  listCoverageEvents,
  type CoverageEventStatus,
  type CoverageEventView,
} from './collectionRunsApi';
import { DataTable } from '@/shared/ui/DataTable';
import { FilterBar, type FilterOption } from '@/shared/ui/FilterBar';
import { StatusBadge } from '@/shared/ui/StatusBadge';
import { Timestamp } from '@/shared/ui/Timestamp';
import { useUrlState } from '@/shared/ui/useUrlState';
import { t } from '@/shared/i18n';

/**
 * Події журналу покриття (ФВ-5.23, ІНТ-3.3, `D-118`) — секція `/admin/sources`.
 *
 * ⛔ Це те, що збір ЗІБРАВ, але в комірки НЕ поклав: період закрито, ручне
 * значення збережено, точок на поле забагато. Такі рядки не прив'язані до
 * прогону (`CollectionRunId = null`, Q-186), тож шухляда прогону їх не
 * показує — і до цієї секції їх не бачив ніхто. Мовчазний пропуск тут
 * найдорожчий: звіт складається, а числа за пізній інтервал у ньому немає.
 *
 * ⚠ Параметр адреси фільтра — `coverageStatus`, а не `status`/`state`: на цій
 * самій сторінці `state` уже належить журналу прогонів (`CollectionRunsPanel`),
 * і спільне ім'я звужувало б обидві секції одним вибором.
 *
 * ⚠ Період показано ключем (`YYYYMM`), як його несе адреса (`?periodKey=`):
 * спільного форматера ключа періоду в `shared/` немає, а підпис «вер. 2026»
 * залежав би від гранулярності періоду, якої подія не несе.
 */
const StatusParam = 'coverageStatus';

export function CoverageEventsPanel(): JSX.Element {
  const [status] = useUrlState(StatusParam);

  const events = useInfiniteQuery({
    queryKey: coverageEventsQueryKey(status),
    queryFn: ({ pageParam }: { pageParam: string | null }) => listCoverageEvents(status, pageParam),
    initialPageParam: null as string | null,
    // ⚠ `last?.` — з тієї самої причини, що в `CollectionRunsPanel`: чужі тести
    // сторінки відповідають `null` на адреси поза своїм інтересом.
    getNextPageParam: (last) => last?.nextCursor ?? null,
  });

  const rows: readonly CoverageEventView[] = (events.data?.pages ?? []).flatMap((page) => page?.items ?? []);

  const statusOptions: readonly FilterOption[] = CoverageEventStatuses.map((value) => ({
    value,
    label: statusFilterLabel(value),
  }));

  return (
    <Stack gap="sm" data-coverage-events-panel="">
      <Title order={2} size="h4">
        {t('coverageEvents.title')}
      </Title>
      <Text size="sm" c="dimmed">
        {t('coverageEvents.hint')}
      </Text>

      <FilterBar filters={[{ id: StatusParam, label: t('coverageEvents.filterStatus'), options: statusOptions }]} />

      <DataTable<CoverageEventView>
        columns={[
          {
            key: 'entity',
            label: t('coverageEvents.entity'),
            minWidth: 200,
            sortValue: (row) => row.sourceEntityName ?? row.sourceEntityCode,
            render: (row) => (
              <Stack gap="xs">
                <Text size="sm">{row.sourceEntityName ?? row.sourceEntityCode}</Text>
                <Text size="xs" c="dimmed" ff="monospace">
                  {row.dataSourceCode}
                </Text>
              </Stack>
            ),
          },
          {
            key: 'periodKey',
            label: t('coverageEvents.period'),
            render: (row) => <Text ff="monospace">{row.periodKey ?? '—'}</Text>,
          },
          {
            key: 'status',
            label: t('coverageEvents.status'),
            render: (row) => <StatusBadge kind="coverage" state={row.status} />,
          },
          {
            key: 'details',
            label: t('coverageEvents.details'),
            sortable: false,
            minWidth: 260,
            // ⚠ Серверний текст як є — той самий прийом, що `errorMessage` у
            // `CollectionRunDetailDrawer`: пояснення пише сервер, без стеків (ФВ-6.11).
            render: (row) => (
              <Text size="sm" style={{ whiteSpace: 'pre-wrap' }}>
                {row.details ?? '—'}
              </Text>
            ),
          },
          {
            key: 'at',
            label: t('coverageEvents.at'),
            render: (row) => <Timestamp value={row.at} />,
          },
        ]}
        rows={rows}
        rowKey={(row) => String(row.id)}
        isPending={events.isPending}
        error={events.error}
        onRetry={() => void events.refetch()}
        emptyTitle={t('coverageEvents.empty')}
        emptyHint={t('coverageEvents.emptyHint')}
      />

      {events.hasNextPage && (
        <Group justify="center">
          <Button
            size="xs"
            variant="default"
            loading={events.isFetchingNextPage}
            onClick={() => void events.fetchNextPage()}
            data-coverage-events-more=""
          >
            {t('coverageEvents.more')}
          </Button>
        </Group>
      )}
    </Stack>
  );
}

/**
 * Підпис варіанта фільтра — ті самі рядки, що в бейджі (`status.coverage.*`),
 * але літеральними `t(...)`: ключ зі змінної вимагав би запису в
 * `EndpointCoverageTests.DynamicKeySites`.
 */
function statusFilterLabel(status: CoverageEventStatus): string {
  switch (status) {
    case 'SkippedPointCeiling':
      return t('status.coverage.SkippedPointCeiling');
    case 'SkippedPeriodClosed':
      return t('status.coverage.SkippedPeriodClosed');
    case 'ConflictKeptManual':
      return t('status.coverage.ConflictKeptManual');
    default:
      return status;
  }
}
