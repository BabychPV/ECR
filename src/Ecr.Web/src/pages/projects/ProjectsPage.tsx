import { Suspense, lazy, useEffect, useState, type JSX } from 'react';
import { Anchor, Text } from '@mantine/core';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { Link, useNavigate } from 'react-router-dom';
import type { PeriodCalendarDto } from '@/api/types';
import { CollapsibleNote } from '@/features/common/CollapsibleNote';
import { projectsQuery } from '@/features/projects/allProjects';
import {
  calendarYears,
  lockMoment,
  monthList,
  nextLock,
  projectMonthsState,
  type ProjectSummary,
} from '@/features/projects/projectYear';
import { useOpenerFocusReturn } from '@/features/projects/useOpenerFocusReturn';
import { projectTabPath } from '@/features/projects/projectPaths';
import { formatPeriodKey } from '@/shared/format';
import { t } from '@/shared/i18n';
import { can, useSession } from '@/shared/session/useSession';
import { siteMomentText } from '@/shared/siteMoment';
import { DataTable, type DataTableColumn } from '@/shared/ui/DataTable';
import { PageHeader } from '@/shared/ui/PageHeader';
import { useProjectCalendars } from '@/shared/ui/periodStates';
import { StatusBadge } from '@/shared/ui/StatusBadge';
import { TwoLine } from '@/shared/ui/TwoLine';
import { showDone } from '@/shared/ui/notify';

/** Діалог створення — за `import()` (`D-132`): тягне `NumberInput`, а потрібен лише після кліку. */
const CreateProjectModal = lazy(async () => ({
  default: (await import('@/features/projects/CreateProjectModal')).CreateProjectModal,
}));

interface Row {
  readonly project: ProjectSummary;
  readonly calendar: PeriodCalendarDto | undefined;
}

type Period = PeriodCalendarDto['periods'][number];

/** Поточний період: позначений `isCurrent` (`D-77`), інакше перший, що приймає дані. */
function currentOf(calendar: PeriodCalendarDto | undefined): Period | null {
  return (
    calendar?.periods.find((period) => period.isCurrent) ??
    calendar?.periods.find((period) => period.state === 'Open' || period.state === 'Grace') ??
    null
  );
}

function StateCell({ row }: { readonly row: Row }): JSX.Element | null {
  const state = projectMonthsState(row.project, row.calendar);

  if (state.kind === 'unknown') return null;
  const text =
    state.kind === 'notStarted'
      ? t('projects.state.notStarted')
      : state.kind === 'allLocked'
        ? t('projects.state.allLocked')
        : state.months.length === 0
          ? t('projects.state.noneOpen')
          : t('projects.state.open', { months: monthList(state.months) });

  return (
    <Text span size="sm" data-project-state={state.kind} {...(state.kind === 'notStarted' ? { c: 'dimmed' } : {})}>
      {text}
    </Text>
  );
}

function ClosesCell({ row }: { readonly row: Row }): JSX.Element | null {
  if (row.project.status === 'Draft') {
    return (
      <Text span size="sm" c="dimmed">
        {t('projects.closes.afterActivation')}
      </Text>
    );
  }
  const next = row.project.status === 'Active' ? nextLock(row.calendar) : null;
  if (next === null || row.calendar === undefined) {
    return (
      <Text span size="sm" c="dimmed">
        —
      </Text>
    );
  }

  return (
    <TwoLine
      primary={siteMomentText(lockMoment(next), row.calendar.timeZoneId, true) ?? ''}
      secondary={t('projects.closes.locks', { month: formatPeriodKey(next.periodKey, row.calendar.periodKind) })}
    />
  );
}

/**
 * ✎ Projects (макет 08.11, рядок f3): усі проєкти — статус, поточний період, стан місяців і найближче блокування;
 * клац по рядку відкриває проєкт (вкладка Periods). «New project» — створення чернетки, далі вкладка Settings.
 */
export function ProjectsPage(): JSX.Element {
  const session = useSession();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const projects = useQuery({ ...projectsQuery });
  const items = projects.data?.items ?? [];
  // ⛔ N4-08: одночасність запитів календарів обмежена (`useProjectCalendars`); ключ `['periods', id]` спільний
  // із вкладкою Periods, тож відкритий потім проєкт удруге не запитується.
  const calendars = useProjectCalendars(items.map((project) => project.id));
  const calendarFailure = calendars.find((entry) => entry.error !== null)?.error ?? null;

  const [creating, setCreating] = useState(false);
  const [createMounted, setCreateMounted] = useState(false);
  const createFocus = useOpenerFocusReturn();
  useEffect(() => {
    if (creating) setCreateMounted(true);
  }, [creating]);

  const rows: Row[] = items.map((project, index) => ({ project, calendar: calendars[index]?.data }));

  const columns: readonly DataTableColumn<Row>[] = [
    {
      key: 'project',
      label: t('projects.column.project'),
      sortValue: (row) => row.project.code,
      render: (row) => {
        const years = calendarYears(row.calendar);

        return (
          <TwoLine
            primary={
              <Anchor
                component={Link}
                to={projectTabPath(row.project.id, 'periods')}
                ff="monospace"
                fw={500}
                // CL-18 (WCAG 2.5.8): хіт-область — висота контролу KIT (≥ 24 px), як і в колишньому огляді.
                style={{ display: 'inline-flex', alignItems: 'center', minHeight: 'var(--ecr-ctl-height, 28px)' }}
                data-project-open=""
              >
                {row.project.code}
              </Anchor>
            }
            secondary={years.length === 0 ? '' : t('projects.reportingYear', { year: years.join('–') })}
          />
        );
      },
    },
    {
      key: 'status',
      label: t('projects.column.status'),
      sortValue: (row) => row.project.status,
      render: (row) => <StatusBadge kind="project" state={row.project.status} />,
    },
    {
      key: 'current',
      label: t('projects.column.current'),
      sortable: false,
      render: (row) => {
        const current = row.project.status === 'Active' ? currentOf(row.calendar) : null;

        return current === null ? (
          <Text span size="sm" c="dimmed">
            —
          </Text>
        ) : (
          formatPeriodKey(current.periodKey, row.project.periodKind)
        );
      },
    },
    { key: 'state', label: t('projects.column.state'), sortable: false, render: (row) => <StateCell row={row} /> },
    { key: 'closes', label: t('projects.column.closes'), sortable: false, render: (row) => <ClosesCell row={row} /> },
  ];

  return (
    <>
      <PageHeader
        title={t('nav.projects')}
        {...(can(session.data, 'Project.Manage')
          ? {
              primary: {
                label: t('projects.create'),
                onClick: () => {
                  createFocus.remember();
                  setCreating(true);
                },
              },
            }
          : {})}
      />

      <DataTable<Row>
        caption={t('nav.projects')}
        columns={columns}
        rows={projects.isPending ? undefined : rows}
        rowKey={(row) => String(row.project.id)}
        isPending={projects.isPending}
        error={projects.error ?? calendarFailure}
        onRetry={() => void projects.refetch()}
        emptyTitle={t('periods.noProjects')}
        emptyHint={t('periods.noProjectsHint')}
        onRowClick={(row) => void navigate(projectTabPath(row.project.id, 'periods'))}
        rowLabel={(row) => t('projects.openProject', { code: row.project.code })}
        defaultSort={{ key: 'project', direction: 'asc' }}
      />

      <CollapsibleNote noteId="projects-status" summary={t('projects.statusNote.summary')}>
        {t('projects.statusNote')}
      </CollapsibleNote>

      {createMounted && (
        <Suspense fallback={null}>
          <CreateProjectModal
            opened={creating}
            onClose={() => {
              setCreating(false);
              createFocus.restore();
            }}
            onCreated={async (created) => {
              await queryClient.invalidateQueries({ queryKey: projectsQuery.queryKey });
              showDone(t('periods.created'));
              void navigate(projectTabPath(created, 'settings'));
            }}
          />
        </Suspense>
      )}
    </>
  );
}
