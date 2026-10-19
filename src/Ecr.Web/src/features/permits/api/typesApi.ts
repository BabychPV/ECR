import { apiFetch } from '@/api/client';
import type { components } from '@/api/schema';

import type {
  CreatePermitTypeRequest,
  PermitType,
  PermitTypeListParams,
  PublishPermitTypeRequest,
  ReplacePermitTypeRequest,
} from './types';

/*
 * ⛔ Адреса кожної дії записана повністю, а не збирається з помічника: сторож
 * `Кожна_дія_сервера_має_споживача_в_інтерфейсі` шукає літерал `/api/v1/…` разом із методом поруч
 * (Permits, PM-03; інтерфейсу ще немає — його додають PM-06/PM-08).
 */

type PermitTypePage = components['schemas']['PagedResultOfPermitTypeListItemDto'];

/** Перелік типів дозволів зі сторінками (курсор — код останнього типу). Право `Registry.View`. */
export function listPermitTypes(params: PermitTypeListParams = {}): Promise<PermitTypePage> {
  const query = new URLSearchParams();
  if (params.category) query.set('category', params.category);
  if (params.status) query.set('status', params.status);
  if (params.q) query.set('q', params.q);
  if (params.limit) query.set('limit', String(params.limit));
  if (params.cursor) query.set('cursor', params.cursor);
  const path = '/api/v1/permit-types';

  return apiFetch<PermitTypePage>(query.size > 0 ? `${path}?${query.toString()}` : path);
}

/** Повне визначення типу (чернетка, якщо є). `404 ECR-PRM-0404`, якщо типу немає. */
export function getPermitType(code: string): Promise<PermitType> {
  return apiFetch<PermitType>(`/api/v1/permit-types/${encodeURIComponent(code)}`);
}

/** Заводить тип чернеткою. `409 ECR-PRM-4091` — код зайнятий. Право `Permit.EditType`. */
export function createPermitType(body: CreatePermitTypeRequest): Promise<PermitType> {
  return apiFetch<PermitType>('/api/v1/permit-types', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });
}

/**
 * Замінює ЧЕРНЕТКУ визначення цілком. `409 ECR-PRM-4093` — токен `rowVersion` застарів чи немає відкритої
 * чернетки; `422 ECR-PRM-0422` — склад не складається (`messageKey` каже випадок).
 */
export function replacePermitType(code: string, body: ReplacePermitTypeRequest): Promise<PermitType> {
  return apiFetch<PermitType>(`/api/v1/permit-types/${encodeURIComponent(code)}`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });
}

/** Видаляє ніколи не опублікований тип без дозволів. `409 ECR-PRM-4094` — опублікований чи має дозволи. */
export function deletePermitType(code: string): Promise<void> {
  return apiFetch<void>(`/api/v1/permit-types/${encodeURIComponent(code)}`, { method: 'DELETE' });
}

/** Публікує чернетку атомарно (шапка + усі таблиці). Право `Permit.PublishType`. */
export function publishPermitType(code: string, body: PublishPermitTypeRequest): Promise<PermitType> {
  return apiFetch<PermitType>(`/api/v1/permit-types/${encodeURIComponent(code)}/publish`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });
}

/** Відкриває чернетку нової версії опублікованого типу. `409 ECR-PRM-4093` — чернетка вже є чи тип не опублікований. */
export function openPermitTypeVersion(code: string): Promise<PermitType> {
  return apiFetch<PermitType>(`/api/v1/permit-types/${encodeURIComponent(code)}/new-version`, {
    method: 'POST',
  });
}
