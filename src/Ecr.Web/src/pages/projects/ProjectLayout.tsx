import { Suspense, type JSX } from 'react';
import { Button, Center, Skeleton, Stack, Text, Title } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import { Link, NavLink, Outlet, useLocation, useParams } from 'react-router-dom';
import { apiFetch } from '@/api/client';
import type { PeriodCalendarDto } from '@/api/types';
import { projectsQuery } from '@/features/projects/allProjects';
import { ProjectTabs, projectTabPath, type ProjectTabId } from '@/features/projects/projectPaths';
import { calendarYears } from '@/features/projects/projectYear';
import { formatPeriodKey } from '@/shared/format';
import { t } from '@/shared/i18n';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { PageHeader } from '@/shared/ui/PageHeader';
import { StatusBadge } from '@/shared/ui/StatusBadge';
import './projects.css';

/** Підпис вкладки — літералами ключів (сторож каталогу `EndpointCoverageTests` читає літерали). */
function tabLabel(tab: ProjectTabId): string {
  if (tab === 'settings') return t('projects.tab.settings');
  if (tab === 'lifecycle') return t('projects.tab.lifecycle');

  return t('projects.tab.periods');
}

/**
 * ✎ Projects (макет 08.11): проєкт — шапка (код, статус, рік, поточний період) і вкладки Periods · Settings ·
 * Lifecycle. Вкладка — в адресі (`/projects/:id/<вкладка>`), тож посилання на неї лишається робочим.
 *
 * ⚠ Вкладки — навігація посиланнями (`aria-current`), а не `role="tablist"`: кожна — окрема адреса з власною
 * історією «Назад», і вміст змінюється переходом, а не перемиканням панелей на одній сторінці.
 */
export function ProjectLayout(): JSX.Element {
  const projectId = Number(useParams()['id']);
  const { pathname } = useLocation();
  const projects = useQuery({ ...projectsQuery });
  const project = projects.data?.items.find((item) => item.id === projectId);
  // Той самий ключ, що й у вкладки Periods і переліку проєктів (`['periods', id]`) — спільний кеш.
  const calendar = useQuery({
    queryKey: ['periods', projectId],
    queryFn: () => apiFetch<PeriodCalendarDto>(`/api/v1/projects/${String(projectId)}/periods`),
    enabled: project !== undefined,
  });

  if (projects.isSuccess && project === undefined) {
    return (
      <Center mih={240}>
        <Stack align="center" gap="xs" data-project-missing="">
          <Title order={1} size="h3">
            {t('projects.missing', { id: projectId })}
          </Title>
          <Text c="dimmed" size="sm">
            {t('projects.missingHint')}
          </Text>
          <Button component={Link} to="/projects" variant="default">
            {t('projects.allProjects')}
          </Button>
        </Stack>
      </Center>
    );
  }

  const years = calendarYears(calendar.data);
  const current = calendar.data?.periods.find((period) => period.isCurrent) ?? null;
  const meta = [
    years.length === 0 ? null : t('projects.reportingYear', { year: years.join('–') }),
    project?.status === 'Active' && current !== null
      ? t('projects.currentPeriod', { period: formatPeriodKey(current.periodKey, calendar.data?.periodKind) })
      : null,
  ].filter((part): part is string => part !== null);

  return (
    <AsyncBoundary
      isPending={projects.isPending}
      error={projects.error}
      data={projects.data}
      skeleton="table"
      onRetry={() => void projects.refetch()}
    >
      {() => (
        <>
          <PageHeader
            title={project?.code ?? ''}
            back={{ label: t('projects.allProjects'), href: '/projects' }}
            badge={project === undefined ? undefined : <StatusBadge kind="project" state={project.status} />}
            meta={meta.length === 0 ? undefined : <Text size="sm" c="dimmed" data-project-meta="">{meta.join(' · ')}</Text>}
          />
          <nav className="ecr-project-tabs" aria-label={t('projects.sections')}>
            {ProjectTabs.map((tab) => (
              <NavLink key={tab} to={projectTabPath(projectId, tab)} className="ecr-project-tab" data-project-tab={tab}>
                {tabLabel(tab)}
              </NavLink>
            ))}
          </nav>
          {/*
           * ⛔ Перехід між проєктами (Clone → вкладка клону) іде переходом React: поки чанк нової вкладки вантажиться,
           * без власної межі на екрані лишалась СТАРА сторінка — з вкладками, що вели на ДЖЕРЕЛО (там Archive і
           * Delete). Межа з ключем адреси монтується наново на кожну вкладку/проєкт: шапка й вкладки одразу показують
           * проєкт з адреси, а вміст — скелет, доки вантажиться.
           */}
          <Suspense key={pathname} fallback={<Skeleton height={160} radius="sm" data-project-tab-loading="" />}>
            <Outlet />
          </Suspense>
        </>
      )}
    </AsyncBoundary>
  );
}
