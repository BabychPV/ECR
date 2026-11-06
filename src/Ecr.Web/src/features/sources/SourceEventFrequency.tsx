import { useState, type JSX } from 'react';
import { Button, Group, Loader, Select, Stack, Text } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import {
  createCollectionSchedule,
  updateCollectionSchedule,
  type CollectionSchedule,
} from '@/features/integration/scheduleApi';
import { collectionSchedulesKey, useCollectionSchedules } from '@/features/integration/useCollectionSchedules';
import { DataSourcesQueryKey } from '@/features/integration/dataSourcesKey';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { t } from '@/shared/i18n';
import { cronOfFrequency, DefaultEventFrequency, EventFrequencies, frequencyOfCron } from './eventFrequency';

/** Значення `Select` для cron поза набором частот. */
const Custom = 'custom';

/**
 * Частота синку подій PI сутності-шаблону (Q-2, людина 06.11: «це має бути параметр налаштування в UI»).
 *
 * ⚠ Пише той самий розклад збору (`/api/v1/collection-schedules`), що вкладка «Розклад»: розкладу немає — створює
 * (увімкненим), є — міняє лише cron, зберігаючи стан, вікно й залежність. Сервер сам ставить розклад у планувальник,
 * а збір сутності-шаблону подій запускає синк подій (`CollectionJob` → `SourceEventSyncJob`).
 *
 * ⛔ Розкладу немає — синк іде лише вручну й після повернення аркуша; тоді вибрано типову частоту (щогодини) з
 * підказкою «збережіть», а не мовчазне «щогодини», якого насправді немає.
 */
export function SourceEventFrequency({
  sourceEntityId,
  dataSource,
}: {
  readonly sourceEntityId: number;
  readonly dataSource: string;
}): JSX.Element {
  const queryClient = useQueryClient();
  const schedules = useCollectionSchedules(dataSource);
  const [draft, setDraft] = useState<string | null>(null);

  const save = useMutation({
    mutationFn: ({ base, cron }: { base: CollectionSchedule | null; cron: string }) =>
      base === null
        ? createCollectionSchedule({ sourceEntityId, cron, isEnabled: true })
        : updateCollectionSchedule(
            base.id,
            { cron, isEnabled: base.isEnabled, lookbackDays: base.lookbackDays },
            base.rowVersion,
          ),
    onSuccess: (saved) => {
      queryClient.setQueryData<CollectionSchedule[]>(collectionSchedulesKey(dataSource), (rows) => [
        ...(rows ?? []).filter((row) => row.sourceEntityId !== sourceEntityId),
        saved,
      ]);
      void queryClient.invalidateQueries({ queryKey: DataSourcesQueryKey });
      setDraft(null);
      notifications.show({ message: t('schedule.saved') });
    },
    onError: () => void queryClient.invalidateQueries({ queryKey: collectionSchedulesKey(dataSource) }),
  });

  if (schedules.isPending) return <Loader size="sm" role="status" aria-label={t('common.loading')} />;

  if (schedules.isError) {
    return <ErrorAlert error={schedules.error} onRetry={() => void schedules.refetch()} />;
  }

  const schedule = schedules.data.find((row) => row.sourceEntityId === sourceEntityId) ?? null;
  const stored = schedule === null ? null : (frequencyOfCron(schedule.cron) ?? Custom);
  const value = draft ?? stored ?? DefaultEventFrequency;
  const cron = cronOfFrequency(value);
  const dirty = schedule === null || (value !== stored && cron !== undefined);

  const options = EventFrequencies.map((item) => ({ value: item.value, label: t(item.labelKey) }));
  if (stored === Custom && schedule !== null) {
    options.push({ value: Custom, label: t('sourceEvents.frequency.custom', { cron: schedule.cron }) });
  }

  return (
    <Stack gap="xs" data-source-event-frequency={sourceEntityId} data-frequency-stored={stored ?? ''}>
      <Group align="flex-end" gap="sm">
        <Select
          label={t('sourceEvents.frequency.label')}
          description={t('sourceEvents.frequency.hint')}
          data={options}
          value={value}
          allowDeselect={false}
          onChange={(next) => setDraft(next)}
          data-source-event-frequency-select=""
        />
        <Button
          onClick={() => cron !== undefined && save.mutate({ base: schedule, cron })}
          disabled={!dirty || cron === undefined}
          loading={save.isPending}
          data-source-event-frequency-save=""
        >
          {t('sourceEvents.frequency.save')}
        </Button>
      </Group>
      {schedule === null && (
        <Text size="sm" c="dimmed" data-source-event-frequency-none="">
          {t('sourceEvents.frequency.none')}
        </Text>
      )}
      {schedule !== null && !schedule.isEnabled && (
        <Text size="sm" c="dimmed" data-source-event-frequency-off="">
          {t('sources.scheduleOff')}
        </Text>
      )}
      {save.isError && <ErrorAlert error={save.error} />}
    </Stack>
  );
}
