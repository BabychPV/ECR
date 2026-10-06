import { lazy, Suspense, useEffect, useState, type JSX } from 'react';
import { Button, Checkbox, Group, Modal, Progress, Stack, Text, UnstyledButton } from '@mantine/core';
import { useQueryClient } from '@tanstack/react-query';
import { Link, generatePath } from 'react-router-dom';
import type { JobSummary } from '@/api/types';
import { routes } from '@/app/routes';
import { usePendingLoading } from '@/features/common/usePendingLoading';
import { useCancelJob, useRecentJobs } from '@/features/jobs/api';
import { useJobStatus } from '@/features/jobs/useJobStatus';
import { useJobsSummary } from '@/features/jobs/useJobsSummary';
import { JobAttempt, JobResultLink, JobRetry, jobAuthor } from '@/features/jobs/JobFacts';
import { badgeStateOf } from '@/features/workflow/jobFollow';
import { humanizeJobId, jobKindLabel, rawJobId } from '@/features/workflow/jobLabel';
import { formatDecimal } from '@/shared/format';
import { t } from '@/shared/i18n';
import { DataTable, type DataTableColumn } from '@/shared/ui/DataTable';
import { useDetailPanel } from '@/shared/ui/DetailDrawer';
import { FilterBar, type FilterOption } from '@/shared/ui/FilterBar';
import { ListPage } from '@/shared/ui/ListPage';
import type { StatItem, StatStripItems } from '@/shared/ui/StatStrip';
import { StatusBadge, statusKey } from '@/shared/ui/StatusBadge';
import { Timestamp } from '@/shared/ui/Timestamp';
import { TwoLine } from '@/shared/ui/TwoLine';
import { useUrlParamsSetter, useUrlState } from '@/shared/ui/useUrlState';

/** Вміст шторки задачі — лінивим чанком: шторка закрита за замовчуванням (`L2`). */
const JobDetailBody = lazy(() => import('@/features/jobs/JobDetailBody'));

/** Діалог «Find a job by id» — лінивим чанком: потрібен рідко. */
const FindJobModal = lazy(() => import('@/features/jobs/FindJobModal'));

/** Адреса документа задачі — з реєстру маршрутів (`JobFacts` про маршрути не знає). */
function documentHrefOf(id: number): string {
  return generatePath(routes.documentDetail.path, { id: String(id) });
}

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
export function isCancellable(state: string): boolean {
  return CancellableStates.includes(state);
}

/**
 * Стани фільтра «States».
 *
 * ⚠ Підписи — `t(statusKey('job', '…'))` ЛІТЕРАЛАМИ, а не в циклі за змінною:
 * сторож каталогу (`EndpointCoverageTests`) розбирає саме виклик із
 * літералом, і складений ключ лишився б для нього невидимим.
 */
function stateOptions(): readonly FilterOption[] {
  return [
    { value: 'Running', label: t(statusKey('job', 'Running')) },
    { value: 'Queued', label: t(statusKey('job', 'Queued')) },
    { value: 'Failed', label: t(statusKey('job', 'Failed')) },
    { value: 'Succeeded', label: t(statusKey('job', 'Succeeded')) },
    { value: 'SucceededWithErrors', label: t(statusKey('job', 'SucceededWithErrors')) },
    { value: 'FannedOut', label: t(statusKey('job', 'FannedOut')) },
    { value: 'Cancelled', label: t(statusKey('job', 'Cancelled')) },
  ];
}

/** Рядок, за яким шукає поле пошуку: назва, ідентифікатор, автор, документ, повідомлення. */
function searchText(job: JobSummary): string {
  return [
    jobKindLabel(job.jobCode),
    humanizeJobId(job.jobId),
    job.jobId,
    jobAuthor(job.createdByDisplayName),
    job.documentId === null || job.documentId === undefined ? '' : t('jobs.openDocument', { id: job.documentId }),
    job.message ?? '',
  ]
    .join(' ')
    .toLocaleLowerCase();
}

/**
 * Журнал фонових задач (`/admin/jobs`).
 *
 * ⚠ UI-28 (макет `screens-ops.js` `/admin/jobs`, `32-jobs.png`; KIT.md §3):
 * шаблон переліку — пояснення під заголовком, смуга «виконуються / у черзі /
 * провалені», рядок фільтрів, таблиця без кнопок у рядку, шторка задачі
 * `?panel=<jobId>` з причиною провалу, кореляцією й діями.
 *
 * ⛔ До шаблону задачу стежили карткою над журналом, а «Watch», «Cancel job»,
 * «Restart» і «Download» стояли в КОЖНОМУ рядку: колонка дій займала третину
 * ширини, і при 1280 px таблиця вилазила за край (`X-22`). Дії переїхали в
 * підвал шторки — там вони стосуються однієї, явно обраної задачі.
 *
 * ⛔ Ідентифікатор відкритої задачі — в адресі (`?panel=`). Саме це посилання
 * надсилають адміністраторові зі словами «подивись, чому впало». Старі
 * посилання `?id=<jobId>` (`SourcesPage`, `RegistryImpactPage`, листи)
 * перекладаються в `?panel=` — вони й далі відкривають ту саму задачу.
 */
export function JobsPage(): JSX.Element {
  const queryClient = useQueryClient();
  const setParams = useUrlParamsSetter();

  const [panel, setPanel] = useDetailPanel();
  const [legacyId] = useUrlState('id');
  const [mine, setMine] = useUrlState('mine');
  const [query] = useUrlState('q');
  const [type] = useUrlState('type');
  const [state, setState] = useUrlState('state');

  // ⚠ Старе посилання `?id=` — у `?panel=`, заміною історії: «Назад» не має
  // повертати на адресу, яка одразу ж переписується знову.
  useEffect(() => {
    if (legacyId !== null) setParams({ panel: rawJobId(legacyId), id: null });
  }, [legacyId, setParams]);

  // ⛔ `BE-08`. «Лише мої» знятий за замовчуванням — екран відкривається лише з
  // правом `System.ViewHealth`, і для його власника звуження до своїх було б
  // несподіванкою. ✎ UI-28: прапорець — в адресі (`?mine=1`), як і решта
  // фільтрів рядка: «Назад» повертає ціле подання, а не половину.
  const mineOnly = mine === '1';
  const jobs = useRecentJobs(mineOnly);
  const all = jobs.data;

  const [finding, setFinding] = useState(false);

  // ⚠ Підтверджувана задача тримається ЦІЛКОМ, а не самим `jobId`: діалог
  // «справді скасувати?» називає, ЩО саме зупиняється.
  const [confirming, setConfirming] = useState<{ jobId: string } | null>(null);

  // ⚠ Відповідь на скасування — `202`, не новий стан: задача бачить токен і
  // закривається `Cancelled` на найближчій межі батчу. Тому інвалідуються ОБИДВА
  // ключі — перелік і стан задачі в шторці: `['job', id]` не є нащадком
  // `['jobs']`, і одна інвалідація лишила б відкриту шторку зі старим `Running`.
  const cancel = useCancelJob(() => {
    const cancelled = confirming;

    setConfirming(null);
    void queryClient.invalidateQueries({ queryKey: ['jobs'] });
    void queryClient.invalidateQueries({ queryKey: ['jobs-summary'] });

    if (cancelled !== null) {
      void queryClient.invalidateQueries({ queryKey: ['job', cancelled.jobId] });
    }
  });

  // ⚠ `ФВ-14.26`: спінер на кнопці — лише після 100 мс дії, не з першого кадру.
  const cancelLoading = usePendingLoading(cancel.isPending);

  /*
   * ⛔ Смуга — з `GET /jobs/summary` (LS-F), а не з переліку: перелік — лише
   * останні задачі, і лічба по ньому видавала б себе за стан усієї черги.
   * «failed in 24 h» з переліку не порахувати взагалі. Поки лічильників немає
   * (або відмова) — смуги немає (`D15-06`), а не нулі.
   *
   * ⚠ Середня затримка старту — підказкою «у черзі», а не четвертим числом:
   * показник смуги — ціле число, а секунди з десятими в ньому збрехали б
   * округленням. `null` (за добу не стартувало нічого) — підказки немає.
   */
  const counters = useJobsSummary(mineOnly).data;
  const latency = counters?.avgStartLatencyMs;
  const stats: StatStripItems | undefined =
    counters === undefined || counters === null
      ? undefined
      : ([
          { id: 'Running', label: t('jobs.statRunning'), value: counters.running },
          {
            id: 'Queued',
            label: t('jobs.statQueued'),
            value: counters.queued,
            hint:
              latency === null || latency === undefined
                ? undefined
                : t('jobs.statLatency', { seconds: formatDecimal(String(Math.round(latency / 100) / 10)) ?? '' }),
          },
          { id: 'Failed', label: t('jobs.statFailed'), tone: 'danger', value: counters.failed24h },
        ] satisfies readonly [StatItem, StatItem, StatItem]);

  // ⚠ Типи — лише ті, що є в переліку: варіант, який нічого не покаже, — шум.
  const typeOptions: FilterOption[] = [];
  for (const job of all ?? []) {
    if (!typeOptions.some((option) => option.value === job.jobCode)) {
      typeOptions.push({ value: job.jobCode, label: jobKindLabel(job.jobCode) });
    }
  }

  const needle = (query ?? '').trim().toLocaleLowerCase();
  const shown = all?.filter(
    (job) =>
      (needle === '' || searchText(job).includes(needle)) &&
      (type === null || job.jobCode === type) &&
      (state === null || badgeStateOf(job) === state),
  );

  const summary = panel === null ? undefined : all?.find((job) => job.jobId === panel);

  // ⚠ Стан для ПІДВАЛУ шторки — з `GET /jobs/{id}` (той самий запит, що читає
  // лінивий вміст), а поки він летить — з рядка переліку. Шторку відкривають і
  // для задачі поза переліком останніх.
  const detail = useJobStatus(panel);
  const openState = detail.data?.state ?? summary?.state;
  const openBadge = detail.data ?? summary;
  const documentId = detail.data?.documentId ?? summary?.documentId ?? null;
  const resultUrl = detail.data?.resultUrl ?? summary?.resultUrl ?? null;

  const columns: readonly DataTableColumn<JobSummary>[] = [
    {
      // ⚠ Назва задачі — кнопкою шторки: клац по рядку не має клавіатурного
      // шляху. Другий рядок — документ і повідомлення сервера (уже
      // перекладене мовою читача, показується як є).
      key: 'jobCode',
      label: t('jobs.recentCode'),
      sortValue: (job) => jobKindLabel(job.jobCode),
      minWidth: 200,
      render: (job) => {
        const target = [
          job.documentId === null || job.documentId === undefined
            ? ''
            : t('jobs.openDocument', { id: job.documentId }),
          job.message ?? '',
        ]
          .filter((part) => part !== '')
          .join(' · ');

        return (
          <TwoLine
            primary={
              <UnstyledButton
                ta="left"
                fz="sm"
                fw={500}
                data-job-open={job.jobId}
                onClick={(event) => {
                  event.stopPropagation();
                  setPanel(job.jobId);
                }}
              >
                {jobKindLabel(job.jobCode)}
              </UnstyledButton>
            }
            // ⛔ `X-22`: довге повідомлення (ключ експорту на 32 знаки)
            // обрізається з повним текстом у `title`, а не розсуває таблицю.
            secondary={
              target === '' ? undefined : (
                <span className="ecr-ellipsis" title={target} data-job-message="" style={{ maxWidth: 420, display: 'block' }}>
                  {target}
                </span>
              )
            }
          />
        );
      },
    },
    {
      key: 'createdByDisplayName',
      label: t('jobs.createdBy'),
      render: (job) => jobAuthor(job.createdByDisplayName),
      sortValue: (job) => jobAuthor(job.createdByDisplayName),
    },
    {
      /* ⚠ Момент СТАРТУ, не постановки: `JobProgress.Begin` перезаписує цей
         стовпець при запуску. ⛔ `Timestamp` тримає читабельний текст і рівно
         той рядок сервера в `dateTime`/`title` — для звірки з журналом. */
      key: 'startedAt',
      label: t('jobs.recentStarted'),
      render: (job) => <Timestamp value={job.startedAt} />,
    },
    {
      // ⚠ Прогрес — лише поки задача йде; провалена чи скасована каже, де
      // зупинилась. Завершена — порожньо (`D15-06`): 100 % нічого не додають.
      key: 'percent',
      label: t('jobs.progress'),
      sortable: false,
      minWidth: 120,
      render: (job) =>
        job.state === 'Running' ? (
          <Progress value={job.percent} aria-label={`${jobKindLabel(job.jobCode)} · ${t('jobs.progress')}`} />
        ) : job.state === 'Failed' || job.state === 'Cancelled' ? (
          <Text size="xs" c="dimmed">
            {t('jobs.stoppedAt', { percent: job.percent })}
          </Text>
        ) : (
          ''
        ),
    },
    {
      key: 'state',
      label: t('jobs.recentState'),
      render: (job) => (
        <Stack gap="xs">
          <StatusBadge kind="job" state={badgeStateOf(job)} />
          {/* ⚠ «Спроба N з M» — лише з другої спроби: сигнал про ретрай. */}
          <JobAttempt attempt={job.attempt} maxAttempts={job.maxAttempts} />
        </Stack>
      ),
    },
  ];

  const filtered = needle !== '' || type !== null || state !== null;

  return (
    <ListPage
      header={{
        title: t('jobs.title'),
        meta: t('jobs.description'),
        secondary: [
          { label: t('jobs.refresh'), onClick: () => void jobs.refetch() },
          { label: t('jobs.findById'), onClick: () => setFinding(true) },
        ],
      }}
      stats={stats === undefined ? undefined : { label: t('jobs.statsLabel'), items: stats, active: state, onSelect: setState }}
      filters={
        <FilterBar
          search={{ label: t('jobs.search'), placeholder: t('jobs.searchPlaceholder') }}
          filters={[
            { id: 'type', label: t('jobs.filterType'), options: typeOptions },
            { id: 'state', label: t('jobs.filterState'), options: stateOptions() },
          ]}
          clearLabel={t('filters.clear')}
          right={
            /* ⚠ Підказка поруч, а не в назві: знятий прапорець показує ЧУЖІ
               задачі, і без пояснення відмова 403 у того, хто права не має,
               читається як збій екрана, а не як межа доступу. */
            <Group align="center" gap="xs" wrap="nowrap">
              <Checkbox
                label={t('jobs.mineOnly')}
                checked={mineOnly}
                onChange={(event) => setMine(event.currentTarget.checked ? '1' : null)}
              />
              <Text size="xs" c="dimmed">
                {t('jobs.mineOnlyHint')}
              </Text>
            </Group>
          }
        />
      }
      table={
        <DataTable<JobSummary>
          columns={columns}
          rows={shown}
          rowKey={(job) => job.jobId}
          isPending={jobs.isPending}
          error={jobs.error}
          onRetry={() => void jobs.refetch()}
          emptyTitle={t('jobs.recentEmpty')}
          filtered={filtered}
          noMatchTitle={t('jobs.noMatch')}
          onClearFilters={() => setParams({ q: null, type: null, state: null })}
          clearFiltersLabel={t('filters.clear')}
          onRowClick={(job) => setPanel(job.jobId)}
          selectedKey={panel ?? undefined}
        />
      }
      detail={
        panel === null
          ? undefined
          : {
              panelId: panel,
              title: summary === undefined ? humanizeJobId(panel) : jobKindLabel(summary.jobCode),
              subtitle: summary === undefined ? undefined : humanizeJobId(summary.jobId),
              badge: openBadge === undefined ? undefined : <StatusBadge kind="job" state={badgeStateOf(openBadge)} />,
              closeLabel: t('common.close'),
              size: 'lg',
              footer:
                openState === undefined ? undefined : (
                  <>
                    {documentId !== null && (
                      <Button component={Link} to={documentHrefOf(documentId)} variant="default" mr="auto">
                        {t('jobs.openDocument', { id: documentId })}
                      </Button>
                    )}
                    <JobResultLink resultUrl={resultUrl} />
                    {/* ⛔ Лише `Queued`/`Running`: термінальній задачі скасовувати
                        нічого, і сервер відповів би `409` (`ECR-JOB-0409`). */}
                    {isCancellable(openState) && (
                      <Button variant="outline" color="statusError" onClick={() => setConfirming({ jobId: panel })}>
                        {t('jobs.cancel')}
                      </Button>
                    )}
                    {/*
                     * ⛔ `hasViewHealth` — буквально `true`: маршрут `/admin/jobs`
                     * вимагає `System.ViewHealth` (`routes.ts`), тож кожен, хто
                     * бачить шторку, право має. `isOwnJob` — `false` буквально:
                     * перелік може містити чужі задачі, і `true` тут було б
                     * вигадкою. Сервер перевіряє власника сам (`403`).
                     */}
                    <JobRetry
                      jobId={panel}
                      state={openState}
                      isOwnJob={false}
                      hasViewHealth
                      onRestarted={() => {
                        void queryClient.invalidateQueries({ queryKey: ['jobs'] });
                        void queryClient.invalidateQueries({ queryKey: ['jobs-summary'] });
                        void queryClient.invalidateQueries({ queryKey: ['job', panel] });
                      }}
                    />
                  </>
                ),
              children: (
                <Suspense fallback={<Text size="sm" c="dimmed">{t('common.loading')}</Text>}>
                  <JobDetailBody jobId={panel} summary={summary} />
                </Suspense>
              ),
            }
      }
    >
      {finding && (
        <Suspense fallback={null}>
          <FindJobModal
            onClose={() => setFinding(false)}
            onPick={(jobId) => {
              setFinding(false);
              setPanel(jobId);
            }}
          />
        </Suspense>
      )}

      <Modal opened={confirming !== null} onClose={() => setConfirming(null)} title={t('jobs.cancel')}>
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
            loading={cancelLoading}
            onClick={() => {
              // ⛔ L9-37: спінер з'являється лише після 100 мс — до того кнопка
              // активна, і подвійний клік/Enter слав два скасування.
              if (cancel.isPending) return;
              if (confirming !== null) cancel.mutate(confirming.jobId);
            }}
          >
            {cancel.isPending ? t('jobs.cancelling') : t('jobs.cancel')}
          </Button>
        </Group>
      </Modal>
    </ListPage>
  );
}
