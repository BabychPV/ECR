import type { JSX } from 'react';
import { Modal, Stack, Text } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { t } from '@/shared/i18n';
import {
  fetchCellPoints,
  fetchCellSeries,
  type CellRef,
  type CellSourceItem,
  type CellSourceView,
  type RawSeriesView,
} from './api';
import { RawPointsTable } from './RawPointsTable';
import { SeriesChart } from './SeriesChart';

/**
 * Сирі точки, з яких складено значення комірки (`D-463`): згорнутий графік періоду й таблиця точок.
 *
 * ⚠ Вікно — рівно період комірки; ширшого діапазону комірковий вхід сервера не віддає.
 */
export function CellSourceModal({
  cell,
  view,
  source,
  onClose,
}: {
  readonly cell: CellRef;
  readonly view: CellSourceView;
  readonly source: CellSourceItem;
  readonly onClose: () => void;
}): JSX.Element {
  const key = ['cellSource', cell.documentId, cell.tableInstanceId, cell.rowKey, cell.columnDefId, source.mapId] as const;
  const series = useQuery({ queryKey: [...key, 'series'], queryFn: () => fetchCellSeries(cell, source.mapId) });

  return (
    <Modal opened onClose={onClose} size="xl" title={t('cellSource.modalTitle', { tag: source.sourcePath })}>
      <Stack gap="md" data-cell-source-modal="">
        <Text size="xs" c="dimmed">
          {t('cellSource.modalHint', { aggregation: source.aggregation ?? '—', period: String(view.periodKey) })}
        </Text>
        <AsyncBoundary<RawSeriesView>
          isPending={series.isPending}
          error={series.error}
          data={series.data}
          isEmpty={(data) => data.totalPoints === 0}
          emptyTitle={t('piData.pointsEmpty')}
          emptyHint={t('piData.pointsEmptyHint')}
          onRetry={() => void series.refetch()}
        >
          {(data) => <SeriesChart series={data} unit={source.sourceUnitCode} height={200} />}
        </AsyncBoundary>
        <RawPointsTable queryKey={[...key, 'points']} load={(cursor) => fetchCellPoints(cell, source.mapId, cursor)} />
      </Stack>
    </Modal>
  );
}
