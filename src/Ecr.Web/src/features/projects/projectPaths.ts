/** Вкладка проєкту (`/projects/:id/<вкладка>`, макет 08.11). */
export type ProjectTabId = 'periods' | 'settings' | 'lifecycle';

/** Вкладки в порядку показу. */
export const ProjectTabs: readonly ProjectTabId[] = ['periods', 'settings', 'lifecycle'];

/** Адреса вкладки проєкту — одне місце, де вона складається. */
export function projectTabPath(projectId: number, tab: ProjectTabId): string {
  return `/projects/${String(projectId)}/${tab}`;
}
