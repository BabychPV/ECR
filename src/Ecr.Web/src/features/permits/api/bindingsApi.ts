import { apiFetch } from '@/api/client';

import type {
  PermitBinding,
  PermitBindingPreview,
  PermitBindings,
  PermitPreviewParams,
  SavePermitBindingRequest,
} from './bindingsTypes';

/*
 * ⛔ Адреса кожної дії записана повністю, а не збирається з помічника: сторож
 * `Кожна_дія_сервера_має_споживача_в_інтерфейсі` шукає літерал `/api/v1/…` разом із методом поруч
 * (Permits, PM-09; інтерфейсу ще немає — його додає PM-11).
 */

/** Прив'язки версії та ознака редагування (`isEditable = false` для опублікованої). Право `Template.View`. */
export function listPermitBindings(templateVersionId: number): Promise<PermitBindings> {
  return apiFetch<PermitBindings>(`/api/v1/template-versions/${String(templateVersionId)}/permit-bindings`);
}

/** Одна прив'язка за кодом. `404 ECR-PRM-0404`, якщо її немає у версії. */
export function getPermitBinding(templateVersionId: number, code: string): Promise<PermitBinding> {
  return apiFetch<PermitBinding>(
    `/api/v1/template-versions/${String(templateVersionId)}/permit-bindings/${encodeURIComponent(code)}`,
  );
}

/**
 * Створює чи замінює прив'язку чернетки (колонки замінюються цілком). `409 ECR-PRM-4101` — таблицю аркуша зайнято чи код
 * зайнятий; `422 ECR-PRM-0422` — прив'язка не складається (`details.caseKeys` — усі випадки). Право `Template.Edit`.
 */
export function savePermitBinding(
  templateVersionId: number,
  code: string,
  body: SavePermitBindingRequest,
): Promise<PermitBinding> {
  return apiFetch<PermitBinding>(
    `/api/v1/template-versions/${String(templateVersionId)}/permit-bindings/${encodeURIComponent(code)}`,
    {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    },
  );
}

/** Видаляє прив'язку чернетки разом з колонками. `409 ECR-SCHM-0409` — у версії є документи. */
export function deletePermitBinding(templateVersionId: number, code: string): Promise<void> {
  return apiFetch<void>(
    `/api/v1/template-versions/${String(templateVersionId)}/permit-bindings/${encodeURIComponent(code)}`,
    { method: 'DELETE' },
  );
}

/**
 * Перегляд аркуша очима користувача: дозвіл, колонки з ролями, рядки з дозволу, попередження. Дозвіл не знайдено — це
 * `200` із `permit.reasonIfNone`, а не помилка.
 */
export function previewPermitBinding(
  templateVersionId: number,
  code: string,
  params: PermitPreviewParams = {},
): Promise<PermitBindingPreview> {
  const query = new URLSearchParams();
  if (params.documentId !== undefined) query.set('documentId', String(params.documentId));
  if (params.periodKey !== undefined) query.set('periodKey', String(params.periodKey));
  if (params.facilityName) query.set('facilityName', params.facilityName);
  if (params.periodEnd) query.set('periodEnd', params.periodEnd);
  if (params.permitEntryId !== undefined) query.set('permitEntryId', String(params.permitEntryId));
  const path = `/api/v1/template-versions/${String(templateVersionId)}/permit-bindings/${encodeURIComponent(code)}/preview`;

  return apiFetch<PermitBindingPreview>(query.size > 0 ? `${path}?${query.toString()}` : path);
}
