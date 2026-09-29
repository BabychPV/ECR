import { apiFetch } from '@/api/client';
import type { components } from '@/api/schema';

/** Рядок довідника зі значеннями полів (`GET …/rows`, RT-13). */
export type RegistryRow = components['schemas']['RegistryRowDto'];

/** Значення поля рядка: `value` рядком (число — без втрати знаків), `display`, `unit`. */
export type RegistryRowValue = components['schemas']['RegistryRowValueDto'];

/** Сторінка рядків: `items`, `nextCursor` (`null` — кінець), `totalCount`. */
export type RegistryRowsPage = components['schemas']['PagedResultOfRegistryRowDto'];

/** Параметри читання рядків довідника. */
export interface RegistryRowsQuery {
  /** Бізнес-дата чинності `yyyy-MM-dd`; обов'язкова для темпорального довідника. */
  readonly asOf?: string;
  /** Системний момент ISO UTC — значення «станом на» (історія); без нього — поточні. */
  readonly asOfUtc?: string;
  /** Батько композиції (для частини) або каскаду. */
  readonly parentEntryId?: number;
  /** Підрядок коду, назви або текстового поля. */
  readonly q?: string;
  /** Код поля → точне значення в поданні `value`. */
  readonly fields?: Readonly<Record<string, string>>;
  /** Курсор із попередньої сторінки. */
  readonly cursor?: string | null;
  /** Розмір сторінки, 1…500 (сервер відхиляє більше, а не обрізає). */
  readonly limit?: number;
}

/** Типовий розмір сторінки. */
export const REGISTRY_ROWS_PAGE = 100;

/** Рядок запиту: порожні параметри не надсилаються, фільтри полів — `field.<КОД>`. */
export function registryRowsQuery(query: RegistryRowsQuery): string {
  const params = new URLSearchParams();

  if (query.asOf) params.set('asOf', query.asOf);
  if (query.asOfUtc) params.set('asOfUtc', query.asOfUtc);
  if (query.parentEntryId !== undefined) params.set('parentEntryId', String(query.parentEntryId));
  if (query.q?.trim()) params.set('q', query.q.trim());

  for (const [field, value] of Object.entries(query.fields ?? {})) {
    // Очищене поле фільтра — «фільтра немає», а не «порожнє значення».
    if (value.trim()) params.set(`field.${field}`, value.trim());
  }

  if (query.cursor) params.set('cursor', query.cursor);
  params.set('limit', String(query.limit ?? REGISTRY_ROWS_PAGE));

  return params.toString();
}

/**
 * Сторінка рядків довідника зі значеннями (FEATURE-REGISTRY-TABLES §7.1). Споживач — сітка
 * редактора даних (RT-30).
 *
 * ⛔ `version` рядка — непрозорий жетон: його повертають як `baseVersion` у пакеті (RT-14), а не
 * розбирають. Числа приходять рядком і так і лишаються — `Number(...)` зрізав би знаки.
 */
export function getRegistryRows(code: string, query: RegistryRowsQuery = {}): Promise<RegistryRowsPage> {
  return apiFetch<RegistryRowsPage>(
    `/api/v1/registries/${encodeURIComponent(code)}/rows?${registryRowsQuery(query)}`,
  );
}
