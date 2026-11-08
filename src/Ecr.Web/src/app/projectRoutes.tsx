import type { JSX } from 'react';
import { Navigate, useParams, useSearchParams } from 'react-router-dom';
import { projectTabPath, type ProjectTabId } from '@/features/projects/projectPaths';
import { PeriodsPage, ProjectLifecycleTab, ProjectSettingsPage } from './routePrefetch';

/**
 * ✎ Projects (макет 08.11, рядок f2): `/admin/periods` і `/admin/projects` — перенаправлення на «Projects»;
 * `?projectId=N` веде одразу у вкладку цього проєкту (старі посилання й закладки лишаються робочими).
 * `replace` — щоб «Назад» не повертав на адресу, яка одразу перенаправляє знову.
 */
export function LegacyProjectRedirect({ tab }: { readonly tab: ProjectTabId }): JSX.Element {
  const [params] = useSearchParams();
  const projectId = Number(params.get('projectId'));

  return (
    <Navigate to={Number.isInteger(projectId) && projectId > 0 ? projectTabPath(projectId, tab) : '/projects'} replace />
  );
}

/** Вміст вкладки: проєкт — з адреси (`:id`, ціле число стереже `numericParams` у `AppLayout`). */
export function ProjectTab({ tab }: { readonly tab: ProjectTabId }): JSX.Element {
  const projectId = Number(useParams()['id']);

  if (tab === 'settings') return <ProjectSettingsPage projectId={projectId} />;
  if (tab === 'lifecycle') return <ProjectLifecycleTab projectId={projectId} />;

  return <PeriodsPage projectId={projectId} />;
}
