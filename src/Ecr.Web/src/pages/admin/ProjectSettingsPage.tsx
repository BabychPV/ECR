import { Suspense, lazy, useState, type JSX, type ReactNode } from 'react';
import { Button, Group, Modal, Select, Stack, Text, Title } from '@mantine/core';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type { ChangeProjectTimeZoneRequest, PeriodCalendarDto } from '@/api/types';
import { hasProjectGrant } from '@/features/documents/projectGrant';
import { projectsQuery } from '@/features/projects/allProjects';
import { CurrentPeriodSetting } from '@/features/projects/CurrentPeriodSetting';
import { SettingsAction } from '@/features/projects/SettingsAction';
import { timeZones } from '@/features/projects/timeZones';
import { t } from '@/shared/i18n';
import { can, useSession } from '@/shared/session/useSession';
import { showApiError, showDone } from '@/shared/ui/notify';

/* Менеджер політик — за `import()` (`D-132`): тягне `NumberInput`, а потрібен лише після кліку. */
const PeriodPolicyManager = lazy(async () => ({
  default: (await import('@/features/projects/PeriodPolicyManager')).PeriodPolicyManager,
}));

/* ✎ B74 P4 (D-576) + ✎ 24.11: шаблони проєкту з погодженням однією таблицею — за `import()` з тієї ж причини
   (бюджет маршруту). */
const ProjectTemplatesTab = lazy(async () => ({
  default: (await import('@/features/projects/ProjectTemplatesTab')).ProjectTemplatesTab,
}));

/** Вкладка, яку малює сторінка: Templates (шаблони й погодження) або General (період, політика, часовий пояс). */
export type ProjectSettingsView = 'templates' | 'general';

/** Розділ вкладки: заголовок `h2` і вміст. */
function Section({ id, title, children }: { readonly id: string; readonly title: string; readonly children: ReactNode }): JSX.Element {
  return (
    <Stack component="section" gap="xs" mb="lg" aria-labelledby={`project-settings-${id}`} data-project-section={id} maw={880}>
      <Title order={2} size="h4" fz="md" id={`project-settings-${id}`}>
        {title}
      </Title>
      {children}
    </Stack>
  );
}

/**
 * ✎ Projects (макет 08.11, рядок f5): налаштування проєкту. ✎ 24.11 (рішення людини «Одна таблиця шаблонів»):
 * колишня вкладка Settings розділена на дві —
 * - `templates`: маршрут погодження проєкту одним рядком і одна таблиця шаблонів з погодженням (`ProjectTemplatesTab`);
 * - `general`: поточний період (Automatic / Pin), політика закриття місяців і часовий пояс.
 * Життєвий цикл (активація, клон, архів, видалення) — на вкладці Lifecycle.
 *
 * ⛔ `F-19`: право каже, ЩО можна, а грант — НАД ЧИМ. Кожна дія над проєктом вимагає ще й гранта Manage на нього —
 * інакше кнопка обіцяла б 403.
 */
export function ProjectSettingsPage({
  projectId,
  view = 'general',
}: {
  readonly projectId: number;
  readonly view?: ProjectSettingsView;
}): JSX.Element | null {
  const queryClient = useQueryClient();
  const session = useSession();

  // T6/#52: значення поля ПОРОЖНЄ на відкритті (директива ПК-1 №06 §3) — наявний пояс не підказує новий.
  const [changingTimeZone, setChangingTimeZone] = useState(false);
  const [newTimeZoneId, setNewTimeZoneId] = useState<string | null>(null);

  // ⛔ `X-07`: усі сторінки, а не перші 200 мовчки.
  const projects = useQuery({ ...projectsQuery });

  const periods = useQuery({
    queryKey: ['periods', projectId],
    queryFn: () => apiFetch<PeriodCalendarDto>(`/api/v1/projects/${String(projectId)}/periods`),
  });

  const selected = (projects.data?.items ?? []).find((p) => p.id === projectId);

  const refresh = async (): Promise<void> => {
    await queryClient.invalidateQueries({ queryKey: projectsQuery.queryKey });
    await queryClient.invalidateQueries({ queryKey: ['periods', projectId] });
  };

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

  if (selected === undefined) return null;

  const manages = can(session.data, 'Project.Manage');
  const managesSelected = hasProjectGrant(session.data ?? undefined, projectId, 'Manage');
  const managesProject = manages && managesSelected;
  // Закріплення поточного періоду — `Period.Configure` + грант Manage (`SetCurrentPeriodHandler`), як і на Periods.
  const configures = can(session.data, 'Period.Configure') && managesSelected;

  const archivedNote =
    selected.status === 'Archived' ? (
      <Text size="sm" mb="sm" data-settings-readonly="">
        {t('projects.settings.archivedReadOnly')}
      </Text>
    ) : null;

  if (view === 'templates') {
    return (
      <>
        {archivedNote}
        {/* ✎ B74 P4 (D-576): шаблони проєкту — бачить кожен, хто відкрив вкладку (`Document.View`); додати й від'єднати —
            `Project.Manage` + грант Manage і не архівний проєкт. ✎ 24.11: погодження (маршрут проєкту, джерело маршруту
            кожного шаблону, виняток B155) — у тій самій таблиці, з правами маршруту проєкту (`Project.Manage` + Manage). */}
        <Section id="templates" title={t('projects.templates.title')}>
          <Suspense fallback={null}>
            <ProjectTemplatesTab
              projectId={selected.id}
              readOnly={selected.status === 'Archived' || !managesProject}
              managesProject={managesProject}
            />
          </Suspense>
        </Section>
      </>
    );
  }

  return (
    <>
      {archivedNote}

      {/* ✎ Period clarity (п. c): режим поточного періоду — Automatic by date / Pinned to <місяць> (`D-77`). */}
      <Section id="current-period" title={t('projectSettings.currentPeriod')}>
        <CurrentPeriodSetting projectId={selected.id} calendar={periods.data} canConfigure={configures} />
      </Section>

      {/* Політика закриття місяців (T6/#37): зміна одразу оновлює стани місяців на вкладці Periods (рядок e1). */}
      {manages && (
        <Section id="policy" title={t('projects.settings.policy')}>
          <SettingsAction id="policies" description={t('projectSettings.policiesActionDesc')}>
            {(describedBy) => (
              <Suspense fallback={null}>
                <PeriodPolicyManager
                  openerLabel={t('projectSettings.policiesAction')}
                  describedBy={describedBy}
                  // ✎ f5: прев'ю дат — для цього проєкту (його календар уже прочитано вище).
                  calendar={periods.data}
                  projectCode={selected.code}
                />
              </Suspense>
            )}
          </SettingsAction>
        </Section>
      )}

      <Section id="time-zone" title={t('periods.timeZone')}>
        <Text size="sm" data-project-time-zone="">
          {selected.timeZoneId}
        </Text>
        {selected.status === 'Draft' && managesProject ? (
          <SettingsAction id="timezone" description={t('projects.settings.timeZoneDesc')}>
            {(describedBy) => (
              <Button variant="default" onClick={() => setChangingTimeZone(true)} aria-describedby={describedBy}>
                {t('periods.timezoneChange')}
              </Button>
            )}
          </SettingsAction>
        ) : (
          <Text size="xs" c="dimmed">
            {t('projects.settings.timeZoneFixed')}
          </Text>
        )}
      </Section>

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
            disabled={newTimeZoneId === null}
            loading={changeTimeZone.isPending}
            onClick={() => {
              // ⛔ B149 (KIT §6.9): діалог змонтований завжди, «Cancel» пояс не скидає — кнопка закритого `Modal` (~200 мс
              // у DOM) міняла пояс проєкту вже після відмови людини.
              if (!changingTimeZone || changeTimeZone.isPending) return;
              if (newTimeZoneId !== null) changeTimeZone.mutate({ id: selected.id, timeZoneId: newTimeZoneId });
            }}
          >
            {t('periods.timezoneChange')}
          </Button>
        </Group>
      </Modal>
    </>
  );
}
