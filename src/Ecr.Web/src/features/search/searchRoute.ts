import type { SearchHit } from './api';

/**
 * Куди веде збіг пошуку даних (BE-19).
 *
 * ⚠ Сервер маршруту не віддає — лише `kind`/`id`/`code`; адресу будує клієнт
 * за тими самими шаблонами, що в `app/routes.ts`:
 *   документ  → `/documents/:id`
 *   шаблон    → `/admin/templates/:id` (картка шаблону)
 *   довідник  → `/admin/registries/:code/definition` (конструктор; саме туди
 *               веде й перелік довідників, `RegistriesPage.tsx`)
 *
 * ⚠ Документ відкривається БЕЗ `periodKey`: у збігу періоду немає, і
 * `DocumentPage` тоді бере поточний — це чесний дефолт, а не вигаданий період.
 *
 * ⛔ Невідомий `kind` дає `null`, а не здогад: пункт, що веде невідомо куди, —
 * гірший за відсутній. Палітра такі збіги не показує.
 */
export function searchHitRoute(hit: SearchHit): string | null {
  switch (hit.kind) {
    case 'document':
      return `/documents/${String(hit.id)}`;
    case 'template':
      return `/admin/templates/${String(hit.id)}`;
    case 'registry':
      return `/admin/registries/${encodeURIComponent(hit.code)}/definition`;
    default:
      return null;
  }
}

/** Порядок груп у палітрі; він же — перелік відомих `kind`. */
export const searchKinds = ['document', 'template', 'registry'] as const;

export type SearchKind = (typeof searchKinds)[number];
