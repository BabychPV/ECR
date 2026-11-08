import { Suspense, lazy, useState, type JSX, type ReactNode } from 'react';
import { Button, Group, Modal, Select, Stack, Text, Title } from '@mantine/core';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type { ChangeProjectTimeZoneRequest, PeriodCalendarDto } from '@/api/types';
import { hasProjectGrant } from '@/features/documents/projectGrant';
import { projectsQuery } from '@/features/projects/allProjects';
import { ApprovalRouteEditor } from '@/features/projects/ApprovalRouteEditor';
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
 * ✎ Projects (макет 08.11, рядок f5): вкладка Settings проєкту — поточний період (Automatic / Pin), політика
 * закриття місяців, маршрут погодження і часовий пояс. Колишня окрема сторінка «Project settings»; життєвий цикл
 * (активація, клон, архів, видалення) — на вкладці Lifecycle.
 *
 * ⛔ `F-19`: право каже, ЩО можна, а грант — НАД ЧИМ. Кожна дія над проєктом вимагає ще й гранта Manage на нього —
 * інакше кнопка обіцяла б 403.
 */
export function ProjectSettingsPage({ projectId }: { readonly projectId: number }): JSX.Element | null {
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

  return (
    <>
      {selected.status === 'Archived' && (
        <Text size="sm" mb="sm" data-settings-readonly="">
          {t('projects.settings.archivedReadOnly')}
        </Text>
      )}

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
                <PeriodPolicyManager openerLabel={t('projectSettings.policiesAction')} describedBy={describedBy} />
              </Suspense>
            )}
          </SettingsAction>
        </Section>
      )}

      {/* ⛔ Маршрут погодження (`ФВ-5.17`) — налаштування проєкту, не місяця. */}
      {managesProject && (
        <Section id="route" title={t('projects.settings.route')}>
          <SettingsAction id="route" description={t('projectSettings.routeActionDesc')}>
            {(describedBy) => (
              <ApprovalRouteEditor projectId={selected.id} openerLabel={t('projectSettings.routeAction')} describedBy={describedBy} />
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
