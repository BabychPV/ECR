import { Group } from '@mantine/core';
import type { KeyValueItem } from '@/shared/ui/KeyValue';
import { StatusBadge } from '@/shared/ui/StatusBadge';
import { Timestamp } from '@/shared/ui/Timestamp';
import { t } from '@/shared/i18n';
import type { RawCollectionStatus } from './api';

/** Останній прогін, останній успіх і частка покриття. Спільне для «Дані з PI» і «Звідки це значення». */
export function lastCollectionItems(last: RawCollectionStatus, percent?: number): KeyValueItem[] {
  const lastRunAt = last.lastRunFinishedAt ?? last.lastRunStartedAt;
  const items: KeyValueItem[] = [
    {
      label: t('piData.lastRun'),
      value:
        last.lastRunId == null ? (
          t('piData.neverCollected')
        ) : (
          <Group gap="xs" wrap="nowrap">
            {last.lastRunStatus != null && <StatusBadge kind="collectionRun" state={last.lastRunStatus} />}
            <Timestamp value={lastRunAt} />
          </Group>
        ),
    },
    { label: t('piData.lastSucceeded'), value: <Timestamp value={last.lastSucceededAt} /> },
  ];

  if (percent !== undefined) items.push({ label: t('piData.coveredShare'), value: `${String(percent)} %` });

  return items;
}

