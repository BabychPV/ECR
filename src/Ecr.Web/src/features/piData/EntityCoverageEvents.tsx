import type { JSX } from 'react';
import { Text } from '@mantine/core';
import { useInfiniteQuery } from '@tanstack/react-query';
import { collectionRunErrorText } from '@/features/integration/collectionRunMessage';
import type { CoverageEventView } from '@/features/integration/collectionRunsApi';
import { DataTable } from '@/shared/ui/DataTable';
import { StatusBadge } from '@/shared/ui/StatusBadge';
import { Timestamp } from '@/shared/ui/Timestamp';
import { t } from '@/shared/i18n';
import { fetchEntityCoverageEvents } from './api';

/**
 * Чому зібране не лягло в документи — події журналу покриття ОДНІЄЇ сутності (`D-463`), новіші першими.
 *
 * ⚠ Той самий серверний журнал і ті самі бейджі й розгортання конверта, що в секції `/admin/sources`
 * (`CoverageEventsPanel`), лише відфільтровані сутністю, яку людина зараз розглядає.
 */
export function EntityCoverageEvents({ entityId }: { readonly entityId: number }): JSX.Element {
  const events = useInfiniteQuery({
    queryKey: ['piData', 'coverageEvents', entityId],
    queryFn: ({ pageParam }: { pageParam: string | null }) => fetchEntityCoverageEvents(entityId, pageParam),
    initialPageParam: null as string | null,
    getNextPageParam: (last) => last?.nextCursor ?? null,
  });

  const rows: readonly CoverageEventView[] = (events.data?.pages ?? []).flatMap((page) => page?.items ?? []);

  return (
    <DataTable<CoverageEventView>
      caption={t('piData.eventsTitle')}
      columns={[
        { key: 'periodKey', label: t('coverageEvents.period'), mono: true, render: (row) => row.periodKey ?? '—' },
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
          render: (row) => (
            <Text size="sm" style={{ whiteSpace: 'pre-wrap' }}>
              {row.details == null ? '—' : collectionRunErrorText(row.details)}
            </Text>
          ),
        },
        { key: 'at', label: t('coverageEvents.at'), render: (row) => <Timestamp value={row.at} /> },
      ]}
      rows={rows}
      rowKey={(row) => String(row.id)}
      isPending={events.isPending}
      error={events.error}
      onRetry={() => void events.refetch()}
      emptyTitle={t('coverageEvents.empty')}
      emptyHint={t('coverageEvents.emptyHint')}
      {...(events.hasNextPage
        ? {
            onShowMore: () => void events.fetchNextPage(),
            showMoreLabel: t('coverageEvents.more'),
            isFetchingMore: events.isFetchingNextPage,
          }
        : {})}
    />
  );
}
