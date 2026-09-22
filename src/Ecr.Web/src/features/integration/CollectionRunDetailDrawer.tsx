import type { JSX } from 'react';
import { Alert, Stack, Text } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { CollectionRunStateBadge } from './CollectionRunStateBadge';
import { DetailDrawer } from '@/shared/ui/DetailDrawer';
import { KeyValue, type KeyValueItem } from '@/shared/ui/KeyValue';
import { Timestamp } from '@/shared/ui/Timestamp';
import {
  collectionRunDetailQueryKey,
  getCollectionRunDetail,
  type CollectionRunDetail,
  type CollectionRunView,
} from './collectionRunsApi';
import { t } from '@/shared/i18n';

/**
 * Подробиці одного прогону збору (ФВ-5.23): помилка (якщо є) і покриті
 * інтервали (до 500, з банером усічення).
 *
 * ⚠ Заголовок і бейдж стану — з рядка переліку (`run`), який батько вже
 * завантажив: подробиця (`errorMessage`/`coverage`) — окремим запитом за
 * `id`, той самий поділ, що в `DataSourceDrawer` (вкладка розкладів
 * довантажується лише за відкриття, а не заголовок шухляди).
 */
export function CollectionRunDetailDrawer({
  run,
  panelId,
}: {
  readonly run: CollectionRunView;
  readonly panelId: string;
}): JSX.Element {
  const detail = useQuery({
    queryKey: collectionRunDetailQueryKey(run.id),
    queryFn: () => getCollectionRunDetail(run.id),
  });

  return (
    <DetailDrawer
      panelId={panelId}
      title={run.sourceEntityName ?? run.sourceEntityCode}
      subtitle={run.dataSourceCode}
      badge={<CollectionRunStateBadge state={run.status} />}
      closeLabel={t('collectionRuns.closeDetails')}
    >
      <AsyncBoundary<CollectionRunDetail>
        isPending={detail.isPending}
        error={detail.error}
        data={detail.data}
        onRetry={() => void detail.refetch()}
      >
        {(loaded) => <CollectionRunDetailContent run={run} detail={loaded} />}
      </AsyncBoundary>
    </DetailDrawer>
  );
}

function CollectionRunDetailContent({
  run,
  detail,
}: {
  readonly run: CollectionRunView;
  readonly detail: CollectionRunDetail;
}): JSX.Element {
  const items: KeyValueItem[] = [
    { label: t('collectionRuns.range'), value: <RangeValue run={run} /> },
    { label: t('collectionRuns.duration'), value: durationValue(run.durationMs) },
    { label: t('collectionRuns.points'), value: run.pointsRetrieved },
    {
      label: t('collectionRuns.triggeredBy'),
      value: run.triggeredByUserId ?? t('collectionRuns.system'),
    },
  ];

  return (
    <Stack gap="md" data-collection-run-detail="">
      <KeyValue items={items} />

      {/* `D15-06`: текст помилки лише коли він Є — `errorMessage` не
          показується заглушкою «помилок немає» на успішному прогоні. */}
      {detail.errorMessage !== null && (
        <Alert color="statusError" title={t('collectionRuns.error')} role="alert">
          <Text size="sm">{detail.errorMessage}</Text>
        </Alert>
      )}

      <Stack gap="xs" data-collection-run-coverage="">
        <Text fw={600} size="sm">
          {t('collectionRuns.coverage')}
        </Text>

        {/* ⚠ Банер усічення — ПЕРЕД переліком, а не після: людина має знати
            ДО того, як почне рахувати інтервали, що частину з них не показано. */}
        {detail.coverageTruncated && (
          <Alert color="statusWarning" data-coverage-truncated="">
            {t('collectionRuns.coverageTruncated')}
          </Alert>
        )}

        {detail.coverage.length === 0 ? (
          <Text size="sm" c="dimmed">
            {t('collectionRuns.coverageEmpty')}
          </Text>
        ) : (
          <Stack gap="xs" data-coverage-list="">
            {detail.coverage.map((interval, index) => (
              // ⚠ Ключ — індекс: інтервали не мають власного ідентифікатора
              // сервера, а порядок (за зростанням) стабільний для одного
              // прогону й не переставляється переліком.
              <Text key={index} size="xs" ff="monospace">
                <Timestamp value={interval.coveredFrom} /> – <Timestamp value={interval.coveredTo} />
              </Text>
            ))}
          </Stack>
        )}
      </Stack>
    </Stack>
  );
}

function RangeValue({ run }: { readonly run: CollectionRunView }): JSX.Element {
  return (
    <>
      <Timestamp value={run.rangeFrom} /> – <Timestamp value={run.rangeTo} />
    </>
  );
}

function durationValue(durationMs: number | null): string | null {
  if (durationMs === null) return null;

  return t('collectionRuns.durationSeconds', { value: (durationMs / 1000).toFixed(1) });
}
