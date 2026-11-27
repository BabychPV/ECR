/**
 * Вкладка проєкту (`/projects/:id/<вкладка>`, макет 08.11). ✎ 24.11 (рішення людини «Одна таблиця шаблонів»): колишня
 * Settings розділена на Templates (шаблони й погодження) і General (поточний період, політика, часовий пояс); стара
 * адреса `/projects/:id/settings` веде на Templates (`LegacyProjectTabRedirect`).
 */
export type ProjectTabId = 'periods' | 'templates' | 'lifecycle' | 'general';

/** Вкладки в порядку показу. */
export const ProjectTabs: readonly ProjectTabId[] = ['periods', 'templates', 'lifecycle', 'general'];

/** Адреса вкладки проєкту — одне місце, де вона складається. */
export function projectTabPath(projectId: number, tab: ProjectTabId): string {
  return `/projects/${String(projectId)}/${tab}`;
}
