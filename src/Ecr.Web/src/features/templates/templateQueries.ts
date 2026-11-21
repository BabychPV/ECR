import { fetchAllPages, type CursorPage } from '@/api/fetchAllPages';
import { queryKeys } from '@/api/queryKeys';
import type { TemplateSummary, TemplateVersionSummary } from '@/api/types';

/*
 * ⛔ PSS-19c: єдиний опис запитів «усі шаблони» і «усі версії шаблону» — ключ + `queryFn` разом (як `projectsQuery`,
 * AN-91 / L8-10). Ключі `queryKeys.templates.list()` / `versionsOf(id)` читали десяток екранів, кожен зі своїм
 * `queryFn` і `?limit=100`: 101-й шаблон чи версія мовчки не існували для вибору, а спільний ключ означав, що
 * хто перший прочитав, той і визначив, що побачать інші. Сторож — `templateQueries.guard.test.ts`.
 */

/** `GET /api/v1/templates` — усі сторінки (`fetchAllPages`), під ключем `queryKeys.templates.list()`. */
export const templatesListQuery = {
  queryKey: queryKeys.templates.list(),
  queryFn: ({ signal }: { signal?: AbortSignal }): Promise<CursorPage<TemplateSummary>> =>
    fetchAllPages<TemplateSummary>('/api/v1/templates', { signal }),
} as const;

/**
 * `GET /api/v1/templates/{id}/versions` — усі версії шаблону, під ключем `queryKeys.templates.versionsOf(id)`.
 *
 * ⚠ `templateId` приймає `undefined` (шаблон ще не обрано) — як і сам ключ; запит тоді вимикає викликач
 * (`enabled`), а не цей опис.
 */
export function templateVersionsQuery(templateId: number | undefined) {
  return {
    queryKey: queryKeys.templates.versionsOf(templateId),
    queryFn: ({ signal }: { signal?: AbortSignal }): Promise<CursorPage<TemplateVersionSummary>> =>
      fetchAllPages<TemplateVersionSummary>(`/api/v1/templates/${String(templateId)}/versions`, { signal }),
  } as const;
}
