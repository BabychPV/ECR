import { Suspense, lazy, useEffect, useState, type JSX, type ReactNode } from 'react';
import { Alert, Button, Group, Modal, Select, Stack, Text, TextInput, Title } from '@mantine/core';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type {
  ChangeProjectTimeZoneRequest,
  CloneProjectRequest,
  PagedProjects,
  PeriodCalendarDto,
  ProjectIdResponse,
} from '@/api/types';
import { hasProjectGrant } from '@/features/documents/projectGrant';
import { projectsQuery } from '@/features/projects/allProjects';
import { ApprovalRouteEditor } from '@/features/projects/ApprovalRouteEditor';
import { MountedLazyConfirmModal } from '@/features/projects/lazyPeriodsDialogs';
import { timeZones } from '@/features/projects/timeZones';
import { useOpenerFocusReturn } from '@/features/projects/useOpenerFocusReturn';
import { t } from '@/shared/i18n';
import { can, useSession } from '@/shared/session/useSession';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { FilterRow } from '@/shared/ui/FilterBar';
import { PageHeader } from '@/shared/ui/PageHeader';
import { StatusBadge, statusKey } from '@/shared/ui/StatusBadge';
import { showApiError, showDone } from '@/shared/ui/notify';
import { useUrlNumber } from '@/shared/ui/useUrlState';

/*
 * Діалог створення проєкту і менеджер політик — за `import()` (`D-132`): обидва тягнуть `NumberInput`, а потрібні
 * лише після кліку.
 */
const CreateProjectModal = lazy(async () => ({
  default: (await import('@/features/projects/CreateProjectModal')).CreateProjectModal,
}));
const PeriodPolicyManager = lazy(async () => ({
  default: (await import('@/features/projects/PeriodPolicyManager')).PeriodPolicyManager,
}));

/** Розділ сторінки: заголовок `h2` і вміст. */
function Section({ id, title, children }: { readonly id: string; readonly title: string; readonly children: ReactNode }): JSX.Element {
  return (
    <Stack component="section" gap="xs" mb="lg" aria-labelledby={`project-settings-${id}`} data-project-section={id}>
      <Title order={2} size="h4" fz="md" id={`project-settings-${id}`}>
        {title}
      </Title>
      {children}
    </Stack>
  );
}

/**
 * ✎ Period clarity (людина 07.11, п. b): налаштування ПРОЄКТУ — окремо від блокування місяців.
 *
 * Сюди переїхали зі сторінки Periods: маршрут погодження (`ФВ-5.17`), політики періодів (T6/#37), клонування
 * (`ФВ-1.3`), пояс майданчика (T6/#52), а з ними — життєвий цикл проєкту (створення, активація, архівація: «архів
 * лишається з проєктом»). Перерахунок проєкту — на переліку документів. Права — ті самі, що й на старому місці.
 *
 * ⛔ `F-19`: право каже, ЩО можна, а грант — НАД ЧИМ. Кожна дія над конкретним проєктом вимагає ще й гранта Manage
 * на нього — інакше кнопка обіцяла б 403.
 */
export function ProjectSettingsPage(): JSX.Element {
  const [projectId, setProjectId] = useUrlNumber('projectId');
  const queryClient = useQueryClient();
  const session = useSession();

  const [creating, setCreating] = useState(false);
  const [createMounted, setCreateMounted] = useState(false);
  const createFocus = useOpenerFocusReturn();
  useEffect(() => {
    if (creating) setCreateMounted(true);
  }, [creating]);

  const [cloning, setCloning] = useState(false);
  const [cloneCode, setCloneCode] = useState('');

  // T6/#52: значення поля ПОРОЖНЄ на відкритті (директива ПК-1 №06 §3) — наявний пояс не підказує новий.
  const [changingTimeZone, setChangingTimeZone] = useState(false);
  const [newTimeZoneId, setNewTimeZoneId] = useState<string | null>(null);

  // ⚠ Q-287: архівація незворотна — через підтвердження.
  const [archiving, setArchiving] = useState(false);

  // ⛔ `X-25`: активація незворотна (`Draft → Active`) — через підтвердження.
  const [confirmingActivate, setConfirmingActivate] = useState(false);

  // ⛔ `X-07`: усі сторінки, а не перші 200 мовчки.
  const projects = useQuery({ ...projectsQuery });

  const periods = useQuery({
    queryKey: ['periods', projectId],
    queryFn: () => apiFetch<PeriodCalendarDto>(`/api/v1/projects/${projectId ?? 0}/periods`),
    enabled: projectId !== null,
  });

  const selected = (projects.data?.items ?? []).find((p) => p.id === projectId);

  // ⛔ `U-10`: проєкт один — обирається сам; з кількох — вибір людині (автовибір «першого-ліпшого» заборонений).
  const onlyProject = projects.data?.items.length === 1 ? projects.data.items[0] : undefined;
  useEffect(() => {
    if (projectId === null && onlyProject !== undefined) setProjectId(onlyProject.id);
  }, [projectId, onlyProject, setProjectId]);

  // ⚠ Аудит-пас 8, п.2: архівація відмовляє `409 ECR-PRD-0409`, доки є незакритий період — попередження ДО кліку.
  const openPeriods = (periods.data?.periods ?? []).filter((p) => p.state !== 'Closed');
  // ⛔ Стан періодів невідомий — запобіжник деградує в бік ЗАБОРОНИ, а не дозволу.
  const openPeriodsUnknown = periods.error !== null || periods.data === undefined;

  const refresh = async (): Promise<void> => {
    await queryClient.invalidateQueries({ queryKey: projectsQuery.queryKey });
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

  // ⚠ Клон копіює довідники, налаштування і склад аркушів; ДАНІ — ні (`ФВ-1.3`).
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

  // ⛔ Позначка, а не перенесення даних; дозволено, лише коли всі періоди закриті (`D-123`). `Archived` — кінцевий.
  const archive = useMutation({
    mutationFn: (id: number) => apiFetch(`/api/v1/projects/${id}/archive`, { method: 'POST' }),
    onSuccess: async () => {
      await refresh();
      setArchiving(false);
      showDone(t('periods.archived'));
    },
    onError: showApiError,
  });

  // T6/#52: дозволено, поки жоден період не вийшов зі `Scheduled` (ФВ-1.1a); кнопка — лише чернетці як видимий проксі.
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

  const manages = can(session.data, 'Project.Manage');
  const managesSelected = projectId !== null && hasProjectGrant(session.data ?? undefined, projectId, 'Manage');
  const managesProject = manages && managesSelected;

  return (
    <>
      <PageHeader
        title={t('projectSettings.title')}
        actions={
          <FilterRow>
            <Select
              size="xs"
              miw={220}
              label={t('periods.project')}
              placeholder={t('periods.pickProject')}
              data={(projects.data?.items ?? []).map((p) => ({
                value: String(p.id),
                label: `${p.code} · ${t(statusKey('project', p.status))}`,
              }))}
              value={projectId === null ? null : String(projectId)}
              onChange={(value) => setProjectId(value === null ? null : Number(value))}
            />

            {manages && (
              <Button
                onClick={() => {
                  createFocus.remember();
                  setCreating(true);
                }}
              >
                {t('periods.create')}
              </Button>
            )}
          </FilterRow>
        }
      />

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

      {selected !== undefined && (
        <>
          <Section id="project" title={t('projectSettings.project')}>
            <Group gap="xs">
              <Text fw={600}>{selected.code}</Text>
              <StatusBadge kind="project" state={selected.status} />
            </Group>
            <Text size="sm" data-project-time-zone="">
              {t('periods.timeZone')}: {selected.timeZoneId}
            </Text>
            {selected.status === 'Draft' && (
              <Text c="statusWarning" size="sm">
                {t('periods.draftHint')}
              </Text>
            )}

            <Group gap="xs">
              {/* ⛔ Кнопка лише для чернетки: доки проєкт не активовано, періоди лишаються `Scheduled` (`A7-25`). */}
              {selected.status === 'Draft' && managesProject && (
                <Button loading={activate.isPending} onClick={() => setConfirmingActivate(true)}>
                  {t('periods.activate')}
                </Button>
              )}
              {selected.status === 'Draft' && managesProject && (
                <Button variant="default" onClick={() => setChangingTimeZone(true)}>
                  {t('periods.timezoneChange')}
                </Button>
              )}
              {managesProject && (
                <Button variant="default" onClick={() => setCloning(true)}>
                  {t('periods.clone')}
                </Button>
              )}
              {/* ⛔ `X-29`: `outline` + `statusError` — кінцева дія червона, а не сіра поруч із «Clone». */}
              {selected.status === 'Active' && managesProject && (
                <Button
                  variant="outline"
                  color="statusError"
                  loading={archive.isPending}
                  onClick={() => setArchiving(true)}
                >
                  {t('periods.archive')}
                </Button>
              )}
            </Group>
          </Section>

          {/* ⛔ Маршрут погодження (`ФВ-5.17`) і політики періодів (T6/#37) — налаштування проєкту, не місяця. */}
          {(managesProject || manages) && (
            <Section id="workflow" title={t('projectSettings.workflow')}>
              <Group gap="xs">
                {managesProject && <ApprovalRouteEditor projectId={selected.id} />}
                {manages && (
                  <Suspense fallback={null}>
                    <PeriodPolicyManager />
                  </Suspense>
                )}
              </Group>
            </Section>
          )}
        </>
      )}

      {createMounted && (
        <Suspense fallback={null}>
          <CreateProjectModal
            opened={creating}
            onClose={() => {
              setCreating(false);
              createFocus.restore();
            }}
            onCreated={async (created) => {
              await refresh();
              setProjectId(created);
              showDone(t('periods.created'));
            }}
          />
        </Suspense>
      )}

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

      <Modal opened={changingTimeZone} onClose={() => setChangingTimeZone(false)} title={t('periods.timezoneChange')}>
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

      <Modal opened={archiving} onClose={() => setArchiving(false)} title={t('periods.archive')}>
        <Text size="sm" mb="sm">
          {t('periods.archiveConfirm')}
        </Text>
        {openPeriods.length > 0 && (
          <Alert color="statusWarning" variant="light" mb="sm">
            {t('periods.archiveOpenPeriods')}
          </Alert>
        )}
        {openPeriodsUnknown && periods.error !== null && (
          <ErrorAlert error={periods.error} onRetry={() => void periods.refetch()} />
        )}
        <Group justify="flex-end" mt="md">
          <Button variant="default" onClick={() => setArchiving(false)}>
            {t('common.cancel')}
          </Button>
          <Button
            color="statusError"
            loading={archive.isPending}
            disabled={openPeriods.length > 0 || openPeriodsUnknown}
            onClick={() => {
              if (selected !== undefined) archive.mutate(selected.id);
            }}
          >
            {t('periods.archive')}
          </Button>
        </Group>
      </Modal>

      <MountedLazyConfirmModal
        opened={confirmingActivate && selected !== undefined}
        title={t('periods.activateTitle', { code: selected?.code ?? '' })}
        text={t('periods.activateConfirm')}
        verb={t('periods.activate')}
        danger={false}
        isPending={activate.isPending}
        onConfirm={() => {
          if (selected !== undefined) activate.mutate(selected.id);
          setConfirmingActivate(false);
        }}
        onClose={() => setConfirmingActivate(false)}
      />
    </>
  );
}
