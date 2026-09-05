import { useState, type JSX } from 'react';
import { Badge, Button, Group, Modal, Select, Table, Text, TextInput } from '@mantine/core';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type {
  CloneProjectRequest,
  CreateProjectRequest,
  PagedProjects,
  PeriodCalendarDto,
  ProjectIdResponse,
  ReopenPeriodRequest,
  SetCurrentPeriodRequest,
} from '@/api/types';
import { can, useSession } from '@/shared/session/useSession';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { LocalizedInput, hasAnyText, type LocalizedValue } from '@/shared/ui/LocalizedInput';
import { PageHeader } from '@/shared/ui/PageHeader';
import { ReasonModal } from '@/shared/ui/ReasonModal';
import { showApiError, showDone } from '@/shared/ui/notify';
import { useUrlNumber } from '@/shared/ui/useUrlState';
import { t } from '@/shared/i18n';

/** Види періоду; значення збігаються з `PeriodKind` домену. */
const PeriodKinds = ['Monthly', 'Quarterly', 'Yearly', 'Custom'];

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
  const [code, setCode] = useState('');
  const [name, setName] = useState<LocalizedValue>({});
  const [periodKind, setPeriodKind] = useState('Monthly');

  const [cloning, setCloning] = useState(false);
  const [cloneCode, setCloneCode] = useState('');

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
   * Створення проєкту.
   *
   * ⛔ Дії не було в інтерфейсі: сторож вважав `POST /projects` досяжним лише
   * тому, що клієнт ЧИТАЄ `GET /projects` тією самою адресою (`A7-42`). А без
   * проєкту немає ані періодів, ані документів — тобто перший крок роботи із
   * системою доводилося робити запитом повз неї.
   *
   * ⚠ Проєкт створюється ЧЕРНЕТКОЮ: періоди лишаються закритими, доки його не
   * активують. Це не зайвий крок — календар будується з рішень, які доти ще
   * можна виправити без сліду в аудиті.
   */
  const create = useMutation({
    mutationFn: () =>
      apiFetch<ProjectIdResponse>('/api/v1/projects', {
        method: 'POST',
        body: JSON.stringify({
          code: code.trim(),
          nameL10n: name,
          periodKind,

          // ⚠ Часовий пояс — ПРОЄКТУ, а не сервера: межі періоду рахуються в
          // ньому (`D-6`). Сервер у Європі не має вирішувати, коли
          // закінчився місяць на місці видобутку.
          timeZoneId: Intl.DateTimeFormat().resolvedOptions().timeZone,
        } satisfies CreateProjectRequest),
      }),
    onSuccess: async (result) => {
      await refresh();
      setCreating(false);
      setCode('');
      setName({});
      setProjectId(result.projectId);
      showDone(t('periods.created'));
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

  const manages = can(session.data, 'Project.Manage');
  const configures = can(session.data, 'Period.Configure');
  const reopens = can(session.data, 'Period.Reopen');

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

            {manages && (
              <Button size="xs" onClick={() => setCreating(true)}>
                {t('periods.create')}
              </Button>
            )}

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

            {selected !== undefined && manages && (
              <Button size="xs" variant="default" onClick={() => setCloning(true)}>
                {t('periods.clone')}
              </Button>
            )}

            {/* ⚠ Архівація пропонується лише активному проєкту: чернетку
                архівувати нема від чого, а вже заархівований — кінцевий стан. */}
            {selected?.status === 'Active' && manages && (
              <Button
                size="xs"
                variant="default"
                color="red"
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
        <Text c="orange" size="sm" mb="xs">
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
                    <Badge ml="xs" size="xs" color="orange" variant="outline">
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
        )}
      </AsyncBoundary>

      <Modal opened={creating} onClose={() => setCreating(false)} title={t('periods.create')}>
        <TextInput
          label={t('periods.code')}
          description={t('periods.codeHint')}
          value={code}
          onChange={(event) => setCode(event.currentTarget.value)}
          data-autofocus
        />

        <LocalizedInput label={t('periods.name')} value={name} onChange={setName} />

        {/* ⛔ Вид періоду задається при створенні і потім визначає весь
            календар: у квартальному проєкті `Sequence` іде від 1 до 4, і
            змінити це згодом означало б переписати ключі всіх даних. */}
        <Select
          mt="sm"
          label={t('periods.kind')}
          description={t('periods.kindHint')}
          data={PeriodKinds}
          value={periodKind}
          onChange={(value) => setPeriodKind(value ?? 'Monthly')}
          allowDeselect={false}
        />

        <Group justify="flex-end" mt="md">
          <Button variant="default" onClick={() => setCreating(false)}>
            {t('common.cancel')}
          </Button>
          <Button
            disabled={code.trim().length === 0 || !hasAnyText(name)}
            loading={create.isPending}
            onClick={() => create.mutate()}
          >
            {t('common.save')}
          </Button>
        </Group>
      </Modal>

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
