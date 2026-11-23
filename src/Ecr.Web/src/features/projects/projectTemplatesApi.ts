import type { QueryClient } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type { components } from '@/api/schema';
import { projectTemplateRoutesKey } from '@/features/approvalRoutes/approvalRouteModel';

/**
 * ✎ B74 P4 (D-576, рішення людини 13.11): шаблони проєкту «рік · об'єкт» — споживач
 * `GET|POST /api/v1/projects/{id}/templates` і `DELETE /api/v1/projects/{id}/templates/{templateId}`.
 *
 * ⚠ Тип — прямо зі схеми: `api/types.ts` — спільний файл.
 */
export type ProjectTemplateDto = components['schemas']['ProjectTemplateDto'];

/**
 * Ключ переліку шаблонів проєкту. Префікс `['projects', id]` — той самий, що в `document-template` майстра
 * документа: приєднання чи від'єднання перечитує обидва одним `invalidateQueries`.
 */
export function projectTemplatesKey(projectId: number): readonly unknown[] {
  return ['projects', projectId, 'templates'];
}

/** `GET /api/v1/projects/{id}/templates` — основний шаблон і приєднані. */
export async function fetchProjectTemplates(projectId: number): Promise<ProjectTemplateDto[]> {
  const rows = await apiFetch<ProjectTemplateDto[] | null>(`/api/v1/projects/${String(projectId)}/templates`);

  // ⚠ Порожнє тіло (`204`, проксі) — порожній перелік, а не падіння рендеру на `.map`.
  return Array.isArray(rows) ? rows : [];
}

/** `POST /api/v1/projects/{id}/templates` — приєднати версію шаблону. */
export function attachProjectTemplate(projectId: number, templateVersionId: number): Promise<ProjectTemplateDto> {
  return apiFetch<ProjectTemplateDto>(`/api/v1/projects/${String(projectId)}/templates`, {
    method: 'POST',
    body: JSON.stringify({ templateVersionId }),
  });
}

/** `DELETE /api/v1/projects/{id}/templates/{templateId}` — від'єднати шаблон (без документів, не основний). */
export function detachProjectTemplate(projectId: number, templateId: number): Promise<void> {
  return apiFetch<void>(`/api/v1/projects/${String(projectId)}/templates/${String(templateId)}`, { method: 'DELETE' });
}

/**
 * Що перечитати після зміни складу шаблонів: усе під `['projects', id]` (перелік шаблонів, склад нового документа
 * за шаблоном) і «Шаблони — погодження» (B155) — там рядок на кожен шаблон проєкту.
 */
export async function invalidateProjectTemplates(client: QueryClient, projectId: number): Promise<void> {
  await Promise.all([
    client.invalidateQueries({ queryKey: ['projects', projectId] }),
    client.invalidateQueries({ queryKey: projectTemplateRoutesKey(projectId) }),
  ]);
}
