import { useState, type JSX } from 'react';
import {
  Button,
  Card,
  Checkbox,
  Group,
  Modal,
  Progress,
  Stack,
  Text,
  TextInput,
} from '@mantine/core';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiEnqueue, apiFetch } from '@/api/client';
import type { JobStatus, JobSummary } from '@/api/types';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { DataTable } from '@/shared/ui/DataTable';
import { FilterBar } from '@/shared/ui/FilterBar';
import { PageHeader } from '@/shared/ui/PageHeader';
import { StatusBadge } from '@/shared/ui/StatusBadge';
import { Timestamp } from '@/shared/ui/Timestamp';
import { useUrlState } from '@/shared/ui/useUrlState';
import { showApiError } from '@/shared/ui/notify';
import { useCancelJob, useRecentJobs } from '@/features/jobs/api';
import { JobAttempt, JobDocumentLink, JobFailure, jobAuthor } from '@/features/jobs/JobFacts';
import { humanizeJobId, jobKindLabel } from '@/features/workflow/jobLabel';
import { t } from '@/shared/i18n';

/**
 * Стани, у яких задачу ще є що скасовувати.
 *
 * ⛔ Перелік звірений з сервером, а не вигаданий: `CancelJobHandler.Active`
 * (`IntegrationHandlers.cs`) містить рівно `Queued` і `Running`, решта
 * (`Succeeded`, `Failed`, `Cancelled`) термінальні й дають `409`
 * (`ECR-JOB-0409`). Кнопка, показана термінальній задачі, — це підтвердження,
 * заздалегідь приречене на відмову сервера.
 */
const CancellableStates: readonly string[] = ['Queued', 'Running'];

/** Чи можна ще просити задачу зупинитися. */
function isCancellable(state: string): boolean {
  return CancellableStates.includes(state);
}

/** Як часто опитувати стан задачі, поки вона виконується. */
const PollMs = 1500;

/**
 * Стеження за фоновою задачею.
 *
 * ⚠ Опитування зупиняється, щойно задача завершилася. Нескінченне опитування
 * завершеної задачі — це запит на секунду від кожної відкритої вкладки, і
 * саме воно перетворює нешкідливий екран на постійне навантаження.
 *
 * ⚠ Невідомий ідентифікатор дає 404, а не порожній стан: інакше клієнт
 * показував би вічний прогрес задачі, якої не існує.
 */
export function JobsPage(): JSX.Element {
  const [input, setInput] = useState('');
  // ⛔ Ідентифікатор задачі — в адресі. Саме це посилання надсилають
  // адміністраторові зі словами «подивись, чому впало»; без нього доводиться
  // диктувати GUID голосом.
  const [jobId, setJobId] = useUrlState('id');

  const job = useQuery({
    queryKey: ['job', jobId],
    queryFn: () => apiFetch<JobStatus>(`/api/v1/jobs/${encodeURIComponent(jobId ?? '')}`),
    enabled: jobId !== null,
    refetchInterval: (query) => {
      const state = query.state.data?.state;

      return state === 'Queued' || state === 'Running' ? PollMs : false;
    },
    retry: false,
  });

  // ⛔ Директива №11, T10 #40. До цього ендпоінта провалена задача, чию
  // причину вже полагодили (недоступне джерело, зайняте з'єднання), можна
  // було повторити лише поставивши НОВУ — і зв'язок зі старим прогресом,
  // на який уже дивиться колега, губився.
  const restart = useMutation({
    mutationFn: () => apiEnqueue(`/api/v1/jobs/${encodeURIComponent(jobId ?? '')}/restart`),
    // ⚠ Той самий jobId — не новий. `refetch`, а не інвалідація: опитування
    // саме підхопить `Queued` і продовжить, як після першої постановки.
    onSuccess: () => void job.refetch(),
    onError: showApiError,
  });

  return (
    <>
      <PageHeader title={t('jobs.title')} />

      <Group align="end" mb="md">
        <TextInput
          label={t('jobs.id')}
          value={input}
          onChange={(event) => setInput(event.currentTarget.value)}
          // ⚠ Без цього Enter у полі не робив нічого — ідентифікатор задачі
          // найчастіше приходить вставленим із чужого повідомлення
          // («подивись, чому впало»), і природний наступний рух — Enter, не
          // потяг миші до кнопки. Той самий обробник, що й клік «Дивитись»:
          // одна дія, два способи її викликати, не дві копії логіки.
          onKeyDown={(event) => {
            if (event.key === 'Enter') setJobId(input.trim().length === 0 ? null : input.trim());
          }}
          miw={280}
          flex="1"
        />
        <Button onClick={() => setJobId(input.trim().length === 0 ? null : input.trim())}>
          {t('jobs.watch')}
        </Button>
      </Group>

      {/*
       * ⚠ Доки ідентифікатор не введено, `data` — `undefined`: обгортка каже
       * «введіть ідентифікатор», а не «задачі немає». Невідомий ідентифікатор
       * дає 404 і показується станом помилки з кодом — саме тому клієнт не
       * малює вічний прогрес задачі, якої не існує.
       */}
      <AsyncBoundary<JobStatus>
        isPending={jobId !== null && job.isPending}
        error={job.error}
        data={jobId === null ? undefined : job.data}
        emptyTitle={t('jobs.pick')}
        emptyHint={t('jobs.pickHint')}
        onRetry={() => void job.refetch()}
      >
        {(status) => (
        <Card withBorder>
          <Stack gap="xs">
            <Group justify="space-between">
              {/* ⛔ Аудит-пас 8, lane6, п.8: `humanizeJobId` лишає GUID
                  екземпляра (копіювати/шукати ним і далі можна), але заміняє
                  сирий `.NET`-тип на людську назву — `IRecalculationJob-a1b2…`
                  замінюється на `Recalculation-a1b2…`. */}
              <Text fw={600}>{humanizeJobId(status.jobId)}</Text>
              {/* ⛔ Тут стояла власна `stateColor`, у якої `default` — СИНІЙ.
                  Тобто `Unknown` і `Unavailable` (планувальник вимкнено або
                  ідентифікатора вже немає — `QuartzJobScheduler.cs`) малювалися
                  тим самим кольором, що й `Queued`: відмова відповісти про
                  задачу виглядала як задача в черзі. Набір дає їм `warning`. */}
              <StatusBadge kind="job" state={status.state} />
            </Group>

            {/* ⚠ BE-08: спроба, момент постановки й документ задачі. Картка —
                місце для всього, що не влізло в сім колонок переліку (L5). */}
            <Group gap="md">
              <JobAttempt attempt={status.attempt} maxAttempts={status.maxAttempts} />
              {status.createdAt !== null && status.createdAt !== undefined && (
                <Text size="xs" c="dimmed">
                  {t('jobs.createdAt')}: <Timestamp value={status.createdAt} />
                </Text>
              )}
              <JobDocumentLink documentId={status.documentId} />
            </Group>

            <Progress value={status.percent} animated={status.state === 'Running'} />

            {status.message !== null && <Text size="sm">{status.message}</Text>}

            <JobFailure
              state={status.state}
              errorCode={status.errorCode}
              correlationId={status.correlationId}
            />

            {/* ⛔ Текст помилки — без стека (ФВ-6.11): стек виносить назовні
                шляхи, імена і подекуди значення. */}
            {status.error !== null && (
              <Text size="sm" c="statusError">
                {status.error}
              </Text>
            )}

            {/* ⛔ Лише для Failed: перезапускати задачу, що виконується чи вже
                успішна, немає сенсу — і сервер (ECR-JOB-0409) це відхилить. */}
            {status.state === 'Failed' && (
              <Group justify="flex-end">
                <Button
                  size="xs"
                  variant="default"
                  loading={restart.isPending}
                  onClick={() => restart.mutate()}
                >
                  {restart.isPending ? t('jobs.restarting') : t('jobs.restart')}
                </Button>
              </Group>
            )}
          </Stack>
        </Card>
        )}
      </AsyncBoundary>

      {/*
       * ⛔ До цього розділу задачу можна було побачити лише знаючи її GUID:
       * збій перерахунку існував у базі й був НЕДОСЯЖНИЙ з інтерфейсу
       * (директива №09 §6.5, `S-25`; `ФВ-12.4`). Перелік — журнал того, що
       * ЩОЙНО сталося, а не архів: рядок клацається і підставляє id вище.
       */}
      <RecentJobs onPick={setJobId} />
    </>
  );
}

function RecentJobs({ onPick }: { onPick: (jobId: string) => void }): JSX.Element {
  const queryClient = useQueryClient();

  // ⚠ Підтверджувана задача тримається в стані ЦІЛКОМ, а не самим `jobId`:
  // заголовок і текст підтвердження називають, ЩО саме зупиняється
  // (`Recalculation-a1b2…`), і діалог «справді скасувати?» без назви задачі —
  // це запит на підтвердження чогось невідомого.
  const [confirming, setConfirming] = useState<JobSummary | null>(null);

  // ⛔ `BE-08`. Прапорець знятий за замовчуванням — цей екран відкривається
  // лише з правом `System.ViewHealth` (`routes.ts`), і для його власника
  // звуження до своїх було б несподіванкою. Сама ж дія `mine=true` потрібна
  // ширше: перелік власних задач — єдиний, доступний БЕЗ цього права, і на
  // ньому стоятиме шухляда «Мої задачі» в шапці (директива №15, фронтенд).
  const [mineOnly, setMineOnly] = useState(false);

  const jobs = useRecentJobs(mineOnly);

  // ⚠ Відповідь на скасування — `202`, не новий стан: задача бачить токен і
  // закривається станом `Cancelled` на найближчій межі батчу. Тому після
  // успіху інвалідуються ОБИДВА ключі — і перелік, і картка конкретної задачі
  // вище: `['job', jobId]` не є нащадком `['jobs']` (різні рядки), тож одна
  // інвалідація лишила б відкриту картку зі старим `Running`.
  const cancel = useCancelJob(() => {
    const cancelled = confirming;

    setConfirming(null);
    void queryClient.invalidateQueries({ queryKey: ['jobs'] });

    if (cancelled !== null) {
      void queryClient.invalidateQueries({ queryKey: ['job', cancelled.jobId] });
    }
  });

  return (
    <>
      <Modal
        opened={confirming !== null}
        onClose={() => setConfirming(null)}
        title={t('jobs.cancel')}
      >
        <Text size="sm" mb="sm">
          {t('jobs.cancelConfirm')}
        </Text>

        {confirming !== null && (
          <Text size="sm" fw={600} mb="sm">
            {humanizeJobId(confirming.jobId)}
          </Text>
        )}

        <Group justify="flex-end" mt="md">
          <Button variant="default" onClick={() => setConfirming(null)}>
            {t('common.cancel')}
          </Button>
          <Button
            color="statusError"
            loading={cancel.isPending}
            onClick={() => {
              if (confirming !== null) cancel.mutate(confirming.jobId);
            }}
          >
            {cancel.isPending ? t('jobs.cancelling') : t('jobs.cancel')}
          </Button>
        </Group>
      </Modal>

      {/*
       * ⚠ Підказка поруч, а не в самій назві: знятий прапорець показує ЧУЖІ
       * задачі, і без пояснення відмова 403 у того, хто права не має,
       * читається як збій екрана, а не як межа доступу.
       */}
      {/*
       * ⚠ Прапорець переїхав у правий слот `FilterBar`, а не зник: рядок
       * фільтрів набору тримає СВОЇ поля в адресі, а цей — у `useState`, і
       * змішувати два джерела в одному компоненті означало б, що «Назад»
       * повертає половину подання.
       *
       * ⛔ Перевести його в адресу цей PR НЕ може: варіанти перемикача
       * («усі»/«мої») потребують двох нових рядків каталогу, а `09-seed.sql`
       * зараз змінює сусідня робота — правка туди дала б конфлікт мержу на
       * рівному місці (CLAUDE.md, пріоритет 0). Названо в Next steps.
       */}
      <FilterBar
        right={
          <Group align="center" gap="xs">
            <Checkbox
              label={t('jobs.mineOnly')}
              checked={mineOnly}
              onChange={(event) => setMineOnly(event.currentTarget.checked)}
            />
            <Text size="xs" c="dimmed">
              {t('jobs.mineOnlyHint')}
            </Text>
          </Group>
        }
      />

      {/*
       * ⛔ `DataTable` замінює `AsyncBoundary` + `<Table>` разом, а не лише
       * розмітку: стани «триває», «порожньо» і «відмова» тепер малює він сам,
       * і саме тому тут більше немає `data?.items ?? []` — взірця, через який
       * невдалий запит перетворювався на «даних немає» у п'ятнадцяти областях.
       *
       * ⚠ Сортування прийшло разом із таблицею і його тут раніше не було:
       * шапка стала клікабельною для трьох перших колонок. Колонка дій
       * `sortable: false` — у кнопок немає скалярного значення, і сортування
       * за ними мовчки не робило б нічого.
       */}
      <DataTable<JobSummary>
        columns={[
          {
            key: 'jobCode',
            label: t('jobs.recentCode'),
            render: (job) => (
              <Stack gap="xs">
                <Text size="sm">{jobKindLabel(job.jobCode)}</Text>
                <JobDocumentLink documentId={job.documentId} />
              </Stack>
            ),
            sortValue: (job) => jobKindLabel(job.jobCode),
            minWidth: 180,
          },
          {
            key: 'state',
            label: t('jobs.recentState'),
            /* ⚠ `sortValue` тут НЕ потрібен, і це перевірено мутацією, а не
               вгадано: ключ колонки — `state`, тобто `DataTable` бере
               `row['state']` сам. Зайвий проп виглядав би як необхідний і
               спонукав би копіювати його в колонки, де він теж зайвий. */
            render: (job) => (
              <Stack gap="xs">
                <StatusBadge kind="job" state={job.state} />
                {/* ⚠ У переліку `maxAttempts` немає — лише «спроба N». */}
                <JobAttempt attempt={job.attempt} />
                <JobFailure
                  state={job.state}
                  errorCode={job.errorCode}
                  correlationId={job.correlationId}
                />
              </Stack>
            ),
          },
          {
            /* ⛔ Уже перекладене сервером мовою читача — показується як є.
               `t()` над ним дав би `⟦…⟧` замість тексту. */
            key: 'message',
            label: t('jobs.recentMessage'),
            sortable: false,
            render: (job) => job.message ?? '',
          },
          {
            key: 'createdByDisplayName',
            label: t('jobs.createdBy'),
            render: (job) => jobAuthor(job.createdByDisplayName),
            sortValue: (job) => jobAuthor(job.createdByDisplayName),
          },
          {
            /* ⚠ Постановка, не старт: сусідня колонка `startedAt` перезаписується
               при запуску, ця — ні. `null` — задача за розкладом. */
            key: 'createdAt',
            label: t('jobs.createdAt'),
            render: (job) =>
              job.createdAt === null || job.createdAt === undefined ? (
                ''
              ) : (
                <Timestamp value={job.createdAt} />
              ),
          },
          {
            /* ⚠ Момент СТАРТУ, не постановки: `JobProgress.Begin` перезаписує
               цей стовпець при запуску, і називати його «створено» означало б
               брехати про кожну задачу, що вже працює.

               ⛔ `Timestamp` тримає ОБИДВІ форми одночасно: видимий текст
               читабельний мовою набору, а рівно той рядок, що віддав сервер,
               лишається в `dateTime`/`title` — тобто в DOM, у копії розмітки і
               в e2e-локаторі. Перелік задач читають поруч із журналом аудиту й
               момент із нього копіюють у запит до бази: звіряти є з чим,
               дивитися — на що. */
            key: 'startedAt',
            label: t('jobs.recentStarted'),
            render: (job) => <Timestamp value={job.startedAt} />,
          },
          {
            key: 'actions',
            label: '',
            sortable: false,
            render: (job) => (
              /* ⚠ `wrap="nowrap"`: дві дії в одному рядку таблиці не мають
                 переносити одна одну на другий рядок і рвати висоту рядків. */
              <Group gap="xs" wrap="nowrap">
                <Button variant="subtle" size="xs" onClick={() => onPick(job.jobId)}>
                  {t('jobs.recentWatch')}
                </Button>

                {/* ⛔ Лише `Queued`/`Running`: термінальній задачі скасовувати
                    нічого, і сервер відповів би `409` (`ECR-JOB-0409`) —
                    кнопка, приречена на відмову, гірша за її відсутність. */}
                {isCancellable(job.state) && (
                  <Button
                    variant="subtle"
                    size="xs"
                    color="statusError"
                    onClick={() => setConfirming(job)}
                  >
                    {t('jobs.cancel')}
                  </Button>
                )}
              </Group>
            ),
          },
        ]}
        rows={jobs.data}
        rowKey={(job) => job.jobId}
        isPending={jobs.isPending}
        error={jobs.error}
        onRetry={() => void jobs.refetch()}
        emptyTitle={t('jobs.recentEmpty')}
      />
    </>
  );
}

/*
 * ✎ Тут стояла `stateColor(state)`. Її `default: 'blue'` і був дефектом,
 * який набір закриває: невідомий стан (а `JobStatus.state` доходить до
 * клієнта простим `string` — `schema.d.ts:9452`) мовчки ставав того ж
 * кольору, що й `Queued`. Розподіл станів тепер один на застосунок —
 * `statusTable.job` у `shared/ui/StatusBadge.tsx`, невідоме — `warning`.
 */
