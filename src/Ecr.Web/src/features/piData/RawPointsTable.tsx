import type { JSX } from 'react';
import { Text } from '@mantine/core';
import { useInfiniteQuery } from '@tanstack/react-query';
import { DataTable } from '@/shared/ui/DataTable';
import { Timestamp } from '@/shared/ui/Timestamp';
import { t } from '@/shared/i18n';
import type { RawPointPage, RawPointView } from './api';

/**
 * Таблиця сирих точок PI (`D-463`), новіші першими, сторінками за курсором сервера.
 *
 * ⛔ «Показати ще» дочитує НАСТУПНУ сторінку (keyset за міткою часу), а не все вікно: рік щохвилини —
 * пів мільйона рядків, і таблиця тримає лише те, що людина справді догортала.
 *
 * ⚠ Число показується ДОСЛІВНО (рядок `decimal` із сервера, `exact`) без хвостових нулів масштабу сховища
 * (`decimal(34,16)`): це сире значення джерела в ЙОГО одиниці, і округлення сховало б саме ту різницю, заради
 * якої сюди дивляться.
 */
export function RawPointsTable({
  queryKey,
  load,
}: {
  readonly queryKey: readonly unknown[];
  readonly load: (cursor: string | null) => Promise<RawPointPage>;
}): JSX.Element {
  const points = useInfiniteQuery({
    queryKey,
    queryFn: ({ pageParam }: { pageParam: string | null }) => load(pageParam),
    initialPageParam: null as string | null,
    getNextPageParam: (last) => last?.nextCursor ?? null,
  });

  const rows: readonly RawPointView[] = (points.data?.pages ?? []).flatMap((page) => page?.items ?? []);

  return (
    <DataTable<RawPointView>
      caption={t('piData.pointsCaption')}
      columns={[
        {
          key: 'timestamp',
          label: t('piData.timestamp'),
          render: (row) => <Timestamp value={row.timestamp} precise />,
        },
        {
          key: 'value',
          label: t('piData.value'),
          num: true,
          exact: true,
          sortValue: (row) => row.valueNumeric ?? row.valueString ?? '',
          render: (row) =>
            row.valueNumeric != null ? (
              exactDecimal(row.valueNumeric)
            ) : (
              <Text span ff="monospace">
                {row.valueString ?? '—'}
              </Text>
            ),
        },
        { key: 'unitCode', label: t('piData.unit'), mono: true, render: (row) => row.unitCode ?? '—' },
        { key: 'quality', label: t('piData.quality'), render: (row) => row.quality ?? '—' },
        {
          key: 'retrievedAt',
          label: t('piData.retrievedAt'),
          render: (row) => <Timestamp value={row.retrievedAt} precise />,
        },
        { key: 'collectionRunId', label: t('piData.run'), mono: true, render: (row) => String(row.collectionRunId) },
      ]}
      rows={rows}
      rowKey={(row) => row.timestamp}
      isPending={points.isPending}
      error={points.error}
      onRetry={() => void points.refetch()}
      emptyTitle={t('piData.pointsEmpty')}
      emptyHint={t('piData.pointsEmptyHint')}
      {...(points.hasNextPage
        ? {
            onShowMore: () => void points.fetchNextPage(),
            showMoreLabel: t('piData.more'),
            isFetchingMore: points.isFetchingNextPage,
          }
        : {})}
    />
  );
}

/** Десятковий рядок без хвостових нулів дробу: `12.5000000000000000` → `12.5`, `3.0` → `3`. Значення не змінюється. */
export function exactDecimal(value: string): string {
  return value.includes('.') ? value.replace(/0+$/, '').replace(/\.$/, '') : value;
}
