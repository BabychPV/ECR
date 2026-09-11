import { useEffect, useRef, useState, type JSX } from 'react';
import { Badge, Button, Group, Modal, Select, Table, Text, TextInput } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiEnqueue, apiFetch } from '@/api/client';
import type {
  ChangeProjectTimeZoneRequest,
  CloneProjectRequest,
  JobStatus,
  PagedProjects,
  PeriodCalendarDto,
  ProjectIdResponse,
  ProjectRecalculationRequest,
  ReopenPeriodRequest,
  SetCurrentPeriodRequest,
} from '@/api/types';
import { ApprovalRouteEditor } from '@/features/projects/ApprovalRouteEditor';
import { CreateProjectModal, timeZones } from '@/features/projects/CreateProjectModal';
import { PeriodPolicyManager } from '@/features/projects/PeriodPolicyManager';
import { pollInterval, outcomeOf } from '@/features/workflow/jobFollow';
import { can, useSession } from '@/shared/session/useSession';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { PageHeader } from '@/shared/ui/PageHeader';
import { ReasonModal } from '@/shared/ui/ReasonModal';
import { showApiError, showDone } from '@/shared/ui/notify';
import { useUrlNumber } from '@/shared/ui/useUrlState';
import { t } from '@/shared/i18n';

/**
 * Проєкти і календар їхніх періодів.
 *
 * ⚠ Стан періоду **обчислюється з часу і зсувів**, а не зберігається полем:
 * збережений статус розійшовся б із календарем рівно тоді, коли фонова задача
 * не спрацювала. Тому екран показує те, що віддає сервер, і не рахує сам.
 *
 * ⚠ `PeriodKey = Year*100 + Sequence` (R-A6), і `Sequence` — це **порядковий
 * номер періоду**, а не місяць: у квартальному проєкті їх чотири.
 *
 * ⛔ Життєвий цикл проєкту тут увесь: створити, клонувати з минулого року,
 * активувати, зафіксувати поточний період, заархівувати. До аудиту (`A7-39`,
 * `A7-42`) з нього була одна дія — активація. Створити проєкт, клонувати його
 * чи відкрити закритий період через інтерфейс було неможливо, тобто перший
 * крок роботи із системою доводилося робити повз неї.
 */
export function PeriodsPage(): JSX.Element {
  const [projectId, setProjectId] = useUrlNumber('projectId');
  const queryClient = useQueryClient();
  const session = useSession();

  const [creating, setCreating] = useState(false);

  const [cloning, setCloning] = useState(false);
  const [cloneCode, setCloneCode] = useState('');

  // T6/#52: діалог зміни поясу майданчика; `null` — закрито. Значення поля
  // ПОРОЖНЄ на відкритті — з тієї ж причини, що й пояс у формі створення
  // (директива ПК-1 №06 §3): наявний пояс не має підказувати новий, бо це
  // одна незворотна дія.
  const [changingTimeZone, setChangingTimeZone] = useState(false);
  const [newTimeZoneId, setNewTimeZoneId] = useState<string | null>(null);

  // Який період відкриваємо; `null` — діалог закритий.
  const [reopening, setReopening] = useState<number | null>(null);

  // Який період фіксуємо як поточний; `null` — діалог закритий.
  const [pinning, setPinning] = useState<number | null>(null);

  // ⛔ Проєкти ВИБИРАЮТЬСЯ зі списку, а не вводяться номером. Це не про
  // зручність: без переліку не видно СТАНУ проєкту, а саме він визначає, чи
  // відкриються періоди взагалі (`A7-25`).
  const projects = useQuery({
    queryKey: ['projects'],
    queryFn: () => apiFetch<PagedProjects>('/api/v1/projects?limit=200'),
  });

  const periods = useQuery({
    queryKey: ['periods', projectId],
    queryFn: () => apiFetch<PeriodCalendarDto>(`/api/v1/projects/${projectId ?? 0}/periods`),
    enabled: projectId !== null,
  });

  const selected = (projects.data?.items ?? []).find((p) => p.id === projectId);

  /** Перечитує проєкти і календар після будь-якої зміни. */
  const refresh = async (): Promise<void> => {
    await queryClient.invalidateQueries({ queryKey: ['projects'] });
    await queryClient.invalidateQueries({ queryKey: ['periods', projectId] });
  };

  const activate = useMutation({
    mutationFn: (id: number) => apiFetch(`/api/v1/projects/${id}/activate`, { method: 'POST' }),
    onSuccess: async () => {
      await refresh();
      showDone(t('periods.activated'));
    },
    onError: showApiError,
  });

  /**
   * Клонування проєкту з попереднього року (`ФВ-1.3`).
   *
   * ⚠ Копіюються довідники, налаштування і склад аркушів; **дані — ні**. Саме
   * тому це окрема дія, а не «створити схожий»: минулорічні числа в новому
   * році — не зручність, а неправдива звітність.
   */
  const clone = useMutation({
    mutationFn: (id: number) =>
      apiFetch<ProjectIdResponse>(`/api/v1/projects/${id}/clone`, {
        method: 'POST',
        body: JSON.stringify({ code: cloneCode.trim() } satisfies CloneProjectRequest),
      }),
    onSuccess: async (result) => {
      await refresh();
      setCloning(false);
      setCloneCode('');
      setProjectId(result.projectId);
      showDone(t('periods.cloned'));
    },
    onError: showApiError,
  });

  /**
   * Архівація проєкту.
   *
   * ⛔ Це ПОЗНАЧКА, а не перенесення даних: фізично в `arc.*` їх переносить
   * окрема задача. Дозволено лише коли всі періоди закриті (`D-123`), і
   * відмова `409` каже саме це.
   *
   * ⚠ `Archived` — кінцевий стан; проєкт не видаляється ніколи (`ФВ-1.15`),
   * бо на нього посилаються подані форми, аудит і зрізи.
   */
  const archive = useMutation({
    mutationFn: (id: number) => apiFetch(`/api/v1/projects/${id}/archive`, { method: 'POST' }),
    onSuccess: async () => {
      await refresh();
      showDone(t('periods.archived'));
    },
    onError: showApiError,
  });

  /**
   * Зміна поясу майданчика (T6/#52).
   *
   * ⛔ Домен уже мав повний, протестований `Project.ChangeTimeZone` —
   * прогалина була рівно тут, у відсутньому ендпоінті над ним, не в
   * правилі. Дозволено лише поки жоден період не вийшов зі стану
   * `Scheduled` (ФВ-1.1a); кнопка нижче показується лише чернетці як
   * найближчий видимий проксі цього правила — сервер перевіряє його
   * насправді і відмовляє `ECR-PRD-0409`, якщо проксі колись розійдеться
   * з фактом.
   */
  const changeTimeZone = useMutation({
    mutationFn: (target: { id: number; timeZoneId: string }) =>
      apiFetch(`/api/v1/projects/${target.id}/timezone`, {
        method: 'PUT',
        body: JSON.stringify({ timeZoneId: target.timeZoneId } satisfies ChangeProjectTimeZoneRequest),
      }),
    onSuccess: async () => {
      await refresh();
      setChangingTimeZone(false);
      setNewTimeZoneId(null);
      showDone(t('periods.timezoneChanged'));
    },
    onError: showApiError,
  });

  /**
   * Фіксація поточного періоду (`ФВ-1.13`).
   *
   * ⛔ `CurrentPeriod` — підказка інтерфейсу, а **не** правило доступу
   * (`D-77`): на рішення про право запису вона не впливає взагалі. Тому
   * кнопка не обіцяє «відкрити період», а каже, який період система пропонує
   * за замовчуванням.
   *
   * ⚠ Причина обов'язкова: режим `Pinned` означає, що календар більше не веде
   * поточний період сам, і через місяць «чому в нас досі січень» має мати
   * відповідь.
   */
  const pinPeriod = useMutation({
    mutationFn: (target: { id: number | null; reason: string }) =>
      apiFetch(`/api/v1/projects/${projectId ?? 0}/current-period`, {
        method: 'PUT',
        body: JSON.stringify({
          pinnedPeriodId: target.id,
          reason: target.reason,
        } satisfies SetCurrentPeriodRequest),
      }),
    onSuccess: async () => {
      await refresh();
      setPinning(null);
      showDone(t('periods.pinned'));
    },
    onError: showApiError,
  });

  /**
   * Відкриття закритого періоду (`ФВ-1.10`).
   *
   * ⛔ Відкриття періоду і відкриття документа — РІЗНІ операції з різними
   * правами (`D-67`). Право `Period.Reopen` існувало від Етапу 3, а кнопки не
   * було: закритий період не відкривався з інтерфейсу взагалі, і документ у
   * ньому теж — бо повернення аркуша відхиляється `ECR-PRD-4223`, доки
   * закритий період.
   */
  const reopenPeriod = useMutation({
    mutationFn: (target: { id: number; reason: string }) =>
      apiFetch(`/api/v1/periods/${target.id}/reopen`, {
        method: 'POST',
        body: JSON.stringify({
          reason: target.reason,

          // ⚠ Безстроково. Вікно з датою — окреме поле, і порожнє за
          // замовчуванням воно означало б «відкрити назавжди» мовчки.
          until: null,
        } satisfies ReopenPeriodRequest),
      }),
    onSuccess: async () => {
      await refresh();
      setReopening(null);
      showDone(t('periods.reopened'));
    },
    onError: showApiError,
  });

  /**
   * Перерахунок УСЬОГО проєкту (Q-151/Q-162): постановка в чергу і стеження
   * за нею — той самий прийом, що й у `SheetActions.tsx` для одного
   * документа (директива №09 `W8` п.7): GUID у тості й забуття про задачу —
   * дефект, який тут не повторюємо.
   *
   * ⚠ `periodKey: null` — повний рік, тобто саме та семантика, заради якої
   * `RunCalculationHandler` існував (`RecalculationJob`, Q-162): без цього
   * маршруту оператор не мав звідки поставити перерахунок УСІХ документів
   * проєкту одразу, лише по одному документу за раз.
   */
  const [recalcJobId, setRecalcJobId] = useState<string | null>(null);

  const recalculate = useMutation({
    mutationFn: (id: number) =>
      apiEnqueue(`/api/v1/projects/${id}/recalculate`, {
        periodKey: null,
        approvedByUserId: null,
        approvalReason: null,
      } satisfies ProjectRecalculationRequest),
    onSuccess: (job) => {
      setRecalcJobId(job.jobId);
      showDone(t('workflow.recalcQueued', { job: job.jobId }));
    },
    onError: showApiError,
  });

  const recalcJob = useQuery({
    queryKey: ['job', recalcJobId],
    queryFn: () => apiFetch<JobStatus>(`/api/v1/jobs/${encodeURIComponent(recalcJobId ?? '')}`),
    enabled: recalcJobId !== null,
    refetchInterval: (query) => pollInterval(query.state.data?.state),

    // ⚠ `GET /jobs/{id}` вимагає `System.ViewHealth` (Q-156) — без нього
    // оператор лишається з поставленою задачею, а не з червоним сповіщенням
    // про право, якого він не просив.
    retry: false,
  });

  const recalcOutcome = recalcJobId === null
    ? null
    : outcomeOf(recalcJob.data?.state, recalcJob.isError);

  const recalcRunning = recalcOutcome === 'running';

  const recalcReported = useRef<string | null>(null);

  useEffect(() => {
    if (recalcJobId === null || recalcOutcome === null || recalcOutcome === 'running') return;
    if (recalcOutcome === 'unknown') return;
    if (recalcReported.current === recalcJobId) return;

    recalcReported.current = recalcJobId;

    if (recalcOutcome === 'succeeded') {
      showDone(t('workflow.recalcDone'));

      // ⚠ Сітки документів проєкту перечитуються САМЕ тут, а не на постановці
      // в чергу: раніше означало б показати старі числа під написом
      // «перераховано».
      void queryClient.invalidateQueries({ queryKey: ['table-slice'] });
      void queryClient.invalidateQueries({ queryKey: ['document'] });

      return;
    }

    // ⛔ Q-234: `error`, а не `message`. `JobStatus.Message` несе останній
    // прогрес (`IJobProgress.ReportAsync`) — на відмові він лишається тим,
    // яким був до неї (часто порожній або застаріле «Виконується»), а причину
    // відмови несе `Error` (`FinishAsync(..., errorMessage: ex.Message, ...)`,
    // `QuartzJobAdapter.cs`). Досі показувався порожній чи нерелевантний текст
    // саме тоді, коли оператору найпотрібніша причина.
    notifications.show({
      color: 'statusError',
      message: recalcJob.data?.error ?? t('workflow.recalcFailed'),
    });
  }, [recalcJobId, recalcOutcome, recalcJob.data?.error, queryClient]);

  const manages = can(session.data, 'Project.Manage');
  const configures = can(session.data, 'Period.Configure');
  const reopens = can(session.data, 'Period.Reopen');
  const recalculates = can(session.data, 'Calculation.Recalculate');

  return (
    <>
      <PageHeader
        title={t('periods.title')}
        actions={
          <Group gap="xs" align="end">
            <Select
              size="xs"
              miw={220}
              label={t('periods.project')}
              placeholder={t('periods.pickProject')}
              data={(projects.data?.items ?? []).map((p) => ({
                value: String(p.id),
                label: `${p.code} · ${p.status}`,
              }))}
              value={projectId === null ? null : String(projectId)}
              onChange={(value) => setProjectId(value === null ? null : Number(value))}
            />

            {/* ⛔ Маршрут погодження (`ФВ-5.17`). Дві таблиці існували від
                Етапу 3 і не мали жодного способу наповнення — багатоетапне
                затвердження було конфігурацією, якої неможливо створити. */}
            {selected !== undefined && manages && (
              <ApprovalRouteEditor projectId={selected.id} />
            )}

            {manages && (
              <Button size="xs" onClick={() => setCreating(true)}>
                {t('periods.create')}
              </Button>
            )}

            {/* T6/#37: CRUD політик — без нього завести чи змінити політику
                можна було лише сідингом або рукою DBA. */}
            {manages && <PeriodPolicyManager />}

            {/* ⛔ Кнопка є лише для чернетки. Доки проєкт не активований,
                задача станів до нього не доходить, періоди лишаються
                `Scheduled`, і система відмовляє в кожній комірці з причиною
                «період ще не відкрито» — неправдивою (`A7-25`). */}
            {selected?.status === 'Draft' && manages && (
              <Button
                size="xs"
                loading={activate.isPending}
                onClick={() => activate.mutate(selected.id)}
              >
                {t('periods.activate')}
              </Button>
            )}

            {/* T6/#52: показана лише чернетці — поки жоден період не вийшов
                зі `Scheduled`, зміна безпечна (ФВ-1.1a); сервер перевіряє це
                насправді через `Project.ChangeTimeZone`, кнопка — лише
                видимий проксі. */}
            {selected?.status === 'Draft' && manages && (
              <Button size="xs" variant="default" onClick={() => setChangingTimeZone(true)}>
                {t('periods.timezoneChange')}
              </Button>
            )}

            {selected !== undefined && manages && (
              <Button size="xs" variant="default" onClick={() => setCloning(true)}>
                {t('periods.clone')}
              </Button>
            )}

            {/* ⛔ Q-151/Q-162: перерахунок усього проєкту, а не по документу за
                раз. Кнопка доступна лише активному проєкту — чернетка не має
                жодного документа, який можна було б перерахувати. */}
            {selected?.status === 'Active' && recalculates && (
              <Button
                size="xs"
                variant="default"
                loading={recalculate.isPending || recalcRunning}
                onClick={() => recalculate.mutate(selected.id)}
              >
                {recalcRunning ? t('workflow.recalcRunning') : t('workflow.recalculate')}
              </Button>
            )}

            {/* ⚠ Архівація пропонується лише активному проєкту: чернетку
                архівувати нема від чого, а вже заархівований — кінцевий стан. */}
            {selected?.status === 'Active' && manages && (
              <Button
                size="xs"
                variant="default"
                color="statusError"
                loading={archive.isPending}
                onClick={() => archive.mutate(selected.id)}
              >
                {t('periods.archive')}
              </Button>
            )}
          </Group>
        }
      />

      {/* Недоступний перелік проєктів лишає порожнім сам вибір — це треба
          сказати, а не показати порожній Select. */}
      <AsyncBoundary<PagedProjects>
        isPending={projects.isPending}
        error={projects.error}
        data={projects.data}
        isEmpty={(page) => page.items.length === 0}
        emptyTitle={t('periods.noProjects')}
        emptyHint={t('periods.noProjectsHint')}
        onRetry={() => void projects.refetch()}
      >
        {() => null}
      </AsyncBoundary>

      {selected?.status === 'Draft' && (
        <Text c="statusWarning" size="sm" mb="xs">
          {t('periods.draftHint')}
        </Text>
      )}

      {/*
       * ⚠ Доки проєкт не обрано, `data` — `undefined`: запиту ще не було, і
       * обгортка каже саме це, а не «періодів немає».
       */}
      <AsyncBoundary<PeriodCalendarDto>
        isPending={projectId !== null && periods.isPending}
        error={periods.error}
        data={projectId === null ? undefined : periods.data}
        isEmpty={(calendar) => calendar.periods.length === 0}
        emptyTitle={projectId === null ? t('periods.pickProject') : t('periods.noPeriods')}
        emptyHint={projectId === null ? undefined : t('periods.noPeriodsHint')}
        skeleton="table"
        onRetry={() => void periods.refetch()}
      >
        {(calendar) => (
        <>
        {/* ⛔ Пояс названо ПОРУЧ із межами, а не лише у формі створення
            (директива ПК-1 №06 §3). Колонки нижче показують моменти в поясі
            МАЙДАНЧИКА (`D-68`), і без підпису «01.02 00:00» читається як
            місцевий час того, хто дивиться. Для проєкту на `Asia/Aqtau`
            (+05:00), відкритого з Астани (+06:00), це різниця в годину рівно
            там, де вирішується, встиг чи не встиг. */}
        <Text size="xs" c="dimmed" mb="xs">
          {t('periods.timeZone')}: {calendar.timeZoneId}
        </Text>

        <Table striped className="ecr-sticky-head">
          <Table.Thead>
            <Table.Tr>
              <Table.Th>{t('periods.key')}</Table.Th>
              <Table.Th>{t('periods.sequence')}</Table.Th>
              <Table.Th>{t('periods.range')}</Table.Th>
              <Table.Th>{t('periods.state')}</Table.Th>
              <Table.Th>{t('periods.grace')}</Table.Th>
              <Table.Th />
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {calendar.periods.map((period) => (
              <Table.Tr key={period.periodKey}>
                <Table.Td>
                  {period.periodKey}
                  {period.isCurrent && (
                    <Badge ml="xs" size="xs" variant="light">
                      {t('periods.current')}
                    </Badge>
                  )}
                </Table.Td>
                <Table.Td>{period.sequence}</Table.Td>
                <Table.Td>
                  {period.startsAt} — {period.endsAt}
                </Table.Td>
                <Table.Td>
                  <Badge color={stateColor(period.state)} variant="light">
                    {period.state}
                  </Badge>
                  {/* ⚠ Відкритий понад календар період видно окремо: інакше
                      `Open` після кінця місяця виглядає як несправність
                      календаря, а не як свідоме рішення людини. */}
                  {period.reopenedUntil !== null && (
                    <Badge ml="xs" size="xs" color="statusWarning" variant="outline">
                      {t('periods.reopenedUntil', { until: period.reopenedUntil })}
                    </Badge>
                  )}
                </Table.Td>
                <Table.Td>{period.graceEndsAt ?? '—'}</Table.Td>
                <Table.Td>
                  <Group gap="xs" justify="flex-end">
                    {period.state === 'Closed' && reopens && (
                      <Button
                        size="compact-xs"
                        variant="subtle"
                        onClick={() => setReopening(period.id)}
                      >
                        {t('periods.reopen')}
                      </Button>
                    )}

                    {!period.isCurrent && configures && (
                      <Button
                        size="compact-xs"
                        variant="subtle"
                        onClick={() => setPinning(period.id)}
                      >
                        {t('periods.pin')}
                      </Button>
                    )}
                  </Group>
                </Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
        </>
        )}
      </AsyncBoundary>

      {/* ⛔ Форма створення живе ОКРЕМИМ компонентом (`A7-56`). Вона
          надсилала запит без версії шаблону і без політики періодів, а сервер
          відхиляє створення без них — тобто перший крок роботи із системою не
          працював жодного разу. Окремий компонент дає їй власний тест, який
          дивиться на тіло запиту, а не на те, що діалог відкрився. */}
      <CreateProjectModal
        opened={creating}
        onClose={() => setCreating(false)}
        onCreated={async (projectId) => {
          await refresh();
          setProjectId(projectId);
          showDone(t('periods.created'));
        }}
      />

      <Modal opened={cloning} onClose={() => setCloning(false)} title={t('periods.clone')}>
        <Text size="sm" mb="sm">
          {t('periods.cloneHint')}
        </Text>

        <TextInput
          label={t('periods.code')}
          value={cloneCode}
          onChange={(event) => setCloneCode(event.currentTarget.value)}
          data-autofocus
        />

        <Group justify="flex-end" mt="md">
          <Button variant="default" onClick={() => setCloning(false)}>
            {t('common.cancel')}
          </Button>
          <Button
            disabled={cloneCode.trim().length === 0 || selected === undefined}
            loading={clone.isPending}
            onClick={() => {
              if (selected !== undefined) clone.mutate(selected.id);
            }}
          >
            {t('periods.clone')}
          </Button>
        </Group>
      </Modal>

      {/* T6/#52: значення поля ПОРОЖНЄ на відкритті — той самий вибір, що й
          у формі створення (директива ПК-1 №06 §3): наявний пояс не має
          підказувати новий для незворотної дії. */}
      <Modal
        opened={changingTimeZone}
        onClose={() => setChangingTimeZone(false)}
        title={t('periods.timezoneChange')}
      >
        <Text size="sm" mb="sm">
          {t('periods.timezoneChangeHint')}
        </Text>

        <Select
          required
          searchable
          limit={50}
          label={t('periods.timeZone')}
          data={timeZones()}
          value={newTimeZoneId}
          onChange={setNewTimeZoneId}
          data-autofocus
        />

        <Group justify="flex-end" mt="md">
          <Button variant="default" onClick={() => setChangingTimeZone(false)}>
            {t('common.cancel')}
          </Button>
          <Button
            disabled={newTimeZoneId === null || selected === undefined}
            loading={changeTimeZone.isPending}
            onClick={() => {
              if (selected !== undefined && newTimeZoneId !== null) {
                changeTimeZone.mutate({ id: selected.id, timeZoneId: newTimeZoneId });
              }
            }}
          >
            {t('periods.timezoneChange')}
          </Button>
        </Group>
      </Modal>

      <ReasonModal
        opened={reopening !== null}
        title={t('periods.reopen')}
        label={t('workflow.reason')}
        description={t('periods.reopenHint')}
        confirmLabel={t('periods.reopen')}
        isPending={reopenPeriod.isPending}
        onConfirm={(reason) => {
          if (reopening !== null) reopenPeriod.mutate({ id: reopening, reason });
        }}
        onClose={() => setReopening(null)}
      />

      <ReasonModal
        opened={pinning !== null}
        title={t('periods.pin')}
        label={t('workflow.reason')}
        description={t('periods.pinHint')}
        confirmLabel={t('periods.pin')}
        isPending={pinPeriod.isPending}
        onConfirm={(reason) => {
          if (pinning !== null) pinPeriod.mutate({ id: pinning, reason });
        }}
        onClose={() => setPinning(null)}
      />
    </>
  );
}

/**
 * Колір стану.
 *
 * ⚠ `Grace` виділено окремим кольором, а не зведено до «відкритого»: правка в
 * пільговому строку позначається як пізня (D-70) і виглядає в аудиті інакше.
 */
function stateColor(state: string): string {
  switch (state) {
    case 'Open':
      return 'green';
    case 'Grace':
      return 'yellow';
    case 'Closed':
      return 'gray';
    default:
      return 'blue';
  }
}
