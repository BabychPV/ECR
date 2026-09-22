import { useState, type JSX } from 'react';
import { Alert, Button, Code, Group, Loader, Stack, Switch, Text, TextInput } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { EcrApiError } from '@/api/client';
import {
  createCollectionSchedule,
  deleteCollectionSchedule,
  updateCollectionSchedule,
  type CollectionSchedule,
} from '@/features/integration/scheduleApi';
import { checkCron } from '@/features/integration/cronFormat';
import {
  collectionSchedulesKey,
  useCollectionSchedules,
} from '@/features/integration/useCollectionSchedules';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { Timestamp } from '@/shared/ui/Timestamp';
import { t } from '@/shared/i18n';

/**
 * Розклад збору однієї сутності джерела (ФВ-14.3): переглянути, задати,
 * змінити, прибрати.
 *
 * ⚠ Самодостатній: сам читає й пише розклад, ззовні потрібні лише
 * `sourceEntityId` і код з'єднання (перелік читається з фільтром
 * `?dataSource=` і кешується за ним). Контракт розкладу ключований САМЕ сутністю джерела
 * (`CreateCollectionScheduleRequest.sourceEntityId`), а не з'єднанням: у
 * `CollectionScheduleView` поля з'єднання немає.
 *
 * ⛔ Три стани, які не можна злити в один:
 *  - відмова читання — `ErrorAlert` і НІЯКОЇ форми (`L10`): «розкладу немає»
 *    на місці відмови запропонувало б створити другий розклад поверх
 *    наявного, і сервер відповів би `409`;
 *  - розкладу немає — форма створення;
 *  - розклад є, але планувальник його НЕ поставив (`lastError`) — причина
 *    видима з першого погляду. Розклад, який тихо не ставиться, гірший за
 *    відсутній: екран каже «збір є», а збору не буде жодного разу.
 */
export function CollectionScheduleTab({
  sourceEntityId,
  dataSource,
}: {
  sourceEntityId: number;

  /** Код з'єднання сутності: перелік читається й кешується саме за ним. */
  dataSource: string;
}): JSX.Element {
  const queryClient = useQueryClient();
  const schedules = useCollectionSchedules(dataSource);
  const schedulesKey = collectionSchedulesKey(dataSource);

  /*
   * ⚠ Відмова збереження живе ТУТ, а не у формі: форма перемонтовується, коли
   * змінюється версія рядка (`key` нижче), а відмова «збережено, але не
   * поставлено» (`422`) якраз і приходить разом із новою версією — у формі
   * вона зникла б рівно тоді, коли її треба показати.
   */
  const [failure, setFailure] = useState<unknown>(null);

  const conflict = failure instanceof EcrApiError && failure.problem.status === 409;

  const replaceRow = (next: CollectionSchedule | null, removedId?: number): void => {
    queryClient.setQueryData<CollectionSchedule[]>(schedulesKey, (rows) => {
      const rest = (rows ?? []).filter(
        (row) => row.sourceEntityId !== sourceEntityId && row.id !== removedId,
      );

      return next === null ? rest : [...rest, next];
    });
  };

  const onFailure = (error: unknown): void => {
    setFailure(error);

    // ⛔ На `409` перелік НЕ перечитується: свіжий рядок перемонтував би форму
    // з чужими значеннями й новою версією, і наступний клік «Зберегти» тихо
    // затер би чужу правку. Перечитує людина — кнопкою під причиною.
    //
    // ⚠ Решту відмов — перечитати: `422 collectionScheduleNotApplied` означає,
    // що рядок УЖЕ збережено (з новою версією й `lastError`), а `404` — що
    // його вже немає.
    if (!(error instanceof EcrApiError && error.problem.status === 409)) {
      void queryClient.invalidateQueries({ queryKey: schedulesKey });
    }
  };

  const save = useMutation({
    mutationFn: ({ base, cron, isEnabled }: { base: CollectionSchedule | null; cron: string; isEnabled: boolean }) =>
      base === null
        ? createCollectionSchedule({ sourceEntityId, cron, isEnabled })
        : updateCollectionSchedule(base.id, { cron, isEnabled }, base.rowVersion),
    onMutate: () => setFailure(null),
    onSuccess: (saved) => {
      replaceRow(saved);
      notifications.show({ message: t('schedule.saved') });
    },
    onError: onFailure,
  });

  const remove = useMutation({
    mutationFn: (base: CollectionSchedule) => deleteCollectionSchedule(base.id, base.rowVersion),
    onMutate: () => setFailure(null),
    onSuccess: (_, base) => {
      replaceRow(null, base.id);
      notifications.show({ message: t('schedule.removed') });
    },
    onError: onFailure,
  });

  if (schedules.isPending) return <Loader size="sm" />;

  if (schedules.isError) {
    return <ErrorAlert error={schedules.error} onRetry={() => void schedules.refetch()} />;
  }

  const schedule = schedules.data.find((row) => row.sourceEntityId === sourceEntityId) ?? null;

  const reload = (): void => {
    setFailure(null);
    void schedules.refetch();
  };

  return (
    <Stack gap="sm" data-collection-schedule={sourceEntityId}>
      {failure !== null && (
        <Stack gap="xs">
          <ErrorAlert error={failure} />
          {conflict && (
            <Group gap="xs">
              <Button size="xs" variant="default" onClick={reload}>
                {t('schedule.reload')}
              </Button>
            </Group>
          )}
        </Stack>
      )}

      <ScheduleForm
        // ⚠ Нова версія рядка — нова форма: чернетка завжди відповідає тому
        // рядку, чию версію вона пошле в `If-Match`.
        key={schedule === null ? 'new' : `${schedule.id}:${schedule.rowVersion}`}
        base={schedule}
        busy={save.isPending || remove.isPending}
        blocked={conflict}
        onSave={(cron, isEnabled) => save.mutate({ base: schedule, cron, isEnabled })}
        onRemove={() => {
          if (schedule !== null) remove.mutate(schedule);
        }}
      />
    </Stack>
  );
}

function ScheduleForm({
  base,
  busy,
  blocked,
  onSave,
  onRemove,
}: {
  base: CollectionSchedule | null;
  busy: boolean;
  /** Конфлікт версій не розв'язаний — зберігати поверх нього не можна. */
  blocked: boolean;
  onSave: (cron: string, isEnabled: boolean) => void;
  onRemove: () => void;
}): JSX.Element {
  const [cron, setCron] = useState(base?.cron ?? '');
  const [isEnabled, setEnabled] = useState(base?.isEnabled ?? true);
  const [confirming, setConfirming] = useState(false);

  const problem = checkCron(cron);
  const dirty = base === null || cron !== base.cron || isEnabled !== base.isEnabled;

  return (
    <Stack gap="sm">
      {base === null ? (
        <Text size="sm" c="dimmed">
          {t('schedule.none')}
        </Text>
      ) : (
        <ScheduleState schedule={base} />
      )}

      <TextInput
        label={t('schedule.cron')}
        description={t('schedule.cronHint')}
        value={cron}
        onChange={(event) => setCron(event.currentTarget.value)}
        // ⛔ Причина видима ДО кліку, а кнопка недоступна: сервер відхилив би
        // цей вираз однаково, лише пізніше й після очікування.
        error={problem === null ? undefined : t(problem.key, problem.params)}
        spellCheck={false}
        autoComplete="off"
        ff="monospace"
      />

      <Switch
        label={t('schedule.enabled')}
        checked={isEnabled}
        onChange={(event) => setEnabled(event.currentTarget.checked)}
      />

      <Group justify="space-between" gap="xs">
        {base !== null && !confirming && (
          <Button variant="subtle" color="statusError" onClick={() => setConfirming(true)} disabled={busy}>
            {t('common.delete')}
          </Button>
        )}

        {base !== null && confirming && (
          <Group gap="xs">
            <Text size="sm">{t('schedule.removeConfirm')}</Text>
            <Button
              size="xs"
              color="statusError"
              variant="light"
              loading={busy}
              disabled={blocked}
              onClick={onRemove}
            >
              {t('common.delete')}
            </Button>
            <Button size="xs" variant="default" onClick={() => setConfirming(false)}>
              {t('common.cancel')}
            </Button>
          </Group>
        )}

        <Button
          ml="auto"
          loading={busy}
          disabled={problem !== null || !dirty || blocked}
          // ⛔ Шле рівно те, що в полі: обрізання — справа сервера, і тоді
          // збережене значення збігається з тим, що людина бачила.
          onClick={() => onSave(cron, isEnabled)}
        >
          {base === null ? t('schedule.create') : t('common.save')}
        </Button>
      </Group>
    </Stack>
  );
}

/** Стан уже заведеного розкладу: чи поставлено, коли збирав востаннє. */
function ScheduleState({ schedule }: { schedule: CollectionSchedule }): JSX.Element {
  return (
    <Stack gap="xs">
      {schedule.lastError !== null && (
        // ⚠ Текст причини — сирий текст планувальника, а не рядок каталогу:
        // сервер кладе в `LastError` повідомлення винятку. Тому — у `Code`:
        // він читається як технічна подробиця, а не як речення інтерфейсу.
        <Alert color="statusError" title={t('schedule.notApplied')} data-last-error="">
          <Stack gap="xs">
            <Code block>{schedule.lastError}</Code>
            <Text size="xs">
              <Timestamp value={schedule.lastErrorAt} />
            </Text>
          </Stack>
        </Alert>
      )}

      <Group gap="xs">
        <Text size="sm">{t('schedule.lastRun')}</Text>
        <Text size="sm">
          <Timestamp value={schedule.lastRunAt} fallback={t('sources.never')} />
        </Text>
      </Group>
    </Stack>
  );
}
