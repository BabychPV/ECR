import { apiFetch } from '@/api/client';
import type { components, paths } from '@/api/schema';

/** «Де використано» дозвіл: лічильники і місця (`GET /api/v1/permits/{id}/usages`). */
export type PermitUsage = components['schemas']['PermitUsageDto'];

/** Одне місце: поле шапки (`header`) або комірка рядка таблиці (`cell`) документа. */
export type PermitUsageItem = components['schemas']['PermitUsageItemDto'];

/** Параметри запиту так, як їх описує контракт (`paths['/api/v1/permits/{id}/usages']`). */
export type PermitUsageQuery = NonNullable<paths['/api/v1/permits/{id}/usages']['get']['parameters']['query']>;

/**
 * Документи, у яких дозвіл обрано значенням поля шапки чи колонки таблиці (PERMIT-USAGE, п. 3 плану). Право
 * `Registry.View` і грант читання реєстру шапки типу; у відповідь потрапляють лише документи проєктів, які читач
 * бачить (`Document.View`). `periodKey` - лише цей період; `limit` 1..500 (сервер за замовчуванням дає 200),
 * лічильники рахують усе, `truncated` каже, що перелік обрізано. `404 ECR-PRM-0404`, якщо дозволу немає.
 *
 * ⛔ Адреса записана повністю, а не збирається з помічника: сторож `EndpointCoverageTests` шукає літерал.
 */
export function getPermitUsages(id: number, query: PermitUsageQuery = {}): Promise<PermitUsage> {
  const params = new URLSearchParams();
  if (query.periodKey !== undefined) params.set('periodKey', String(query.periodKey));
  if (query.limit !== undefined) params.set('limit', String(query.limit));
  const path = `/api/v1/permits/${String(id)}/usages`;

  return apiFetch<PermitUsage>(params.size > 0 ? `${path}?${params.toString()}` : path);
}
