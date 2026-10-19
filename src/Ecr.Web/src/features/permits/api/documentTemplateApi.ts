import { apiFetch } from '@/api/client';
import type { components } from '@/api/schema';

/** Склад документа проєкту: версія шаблону, за якою заводяться документи (`templateVersionId`). */
export type ProjectDocumentTemplate = components['schemas']['DocumentTemplateDto'];

/*
 * ⛔ Адреса записана повністю (сторож `Кожна_дія_сервера_має_споживача_в_інтерфейсі`).
 * Права: `Document.Create` + грант `Write` на проєкт (це не `Template.View`). Для «Оновити з дозволу» береться лише
 * версія шаблону проєкту; без права відповідь `403` і смуга просто не показується.
 */
export function getProjectDocumentTemplate(projectId: number): Promise<ProjectDocumentTemplate> {
  return apiFetch<ProjectDocumentTemplate>(`/api/v1/projects/${String(projectId)}/document-template`);
}
