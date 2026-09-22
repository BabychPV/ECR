import { apiFetch, EcrApiError } from '@/api/client';
import type { components } from '@/api/schema';
import type { CreateUnitRequest, UnitRef } from '@/api/types';

/** «Де використовується» — єдина форма `GET /…/{id}/usage` (директива №15, BE-15). */
export type UsageResponse = components['schemas']['UsageResponse'];
export type UsageItem = components['schemas']['UsageItemDto'];

/** Код відмови «на одиницю посилаються». */
const UNIT_IN_USE = 'ECR-UOM-0409';

/*
 * ⛔ Адреса записана повністю, а не збирається з помічника — той самий
 * прийом, що й `features/registries/api.ts`: сторож
 * `Кожна_дія_сервера_має_споживача_в_інтерфейсі` шукає в коді клієнта
 * літерал `/api/v1/…` разом із методом поруч.
 */

/** Де використовується одиниця: перші 20 посилань і загальна кількість. */
export function unitUsage(unitId: number): Promise<UsageResponse> {
  return apiFetch<UsageResponse>(`/api/v1/units/${unitId}/usage`);
}

/**
 * Видаляє одиницю. Одиниця з посиланнями дає `409 ECR-UOM-0409` — це не
 * аварія, а відповідь по суті, і показує її діалог (див. `unitReferences`).
 */
export function deleteUnit(unitId: number): Promise<void> {
  return apiFetch<void>(`/api/v1/units/${unitId}`, { method: 'DELETE' });
}

/**
 * Залежні з відмови `ECR-UOM-0409`; `null` — відмова інша.
 *
 * ⛔ `null`, а не порожній перелік: «ніхто не посилається» сервер не казав.
 */
export function unitReferences(error: unknown): UsageResponse | null {
  if (!(error instanceof EcrApiError) || error.problem.errorCode !== UNIT_IN_USE) {
    return null;
  }

  const references = error.problem.extensions2?.['references'];
  if (!Array.isArray(references)) {
    return null;
  }

  // `total` їде рядком (параметр каталогу повідомлень); перелік — перші 20.
  const total = Number(error.problem.extensions2?.['total']);

  return {
    total: Number.isFinite(total) ? total : references.length,
    items: references as UsageItem[],
  };
}

/** Одиниця для правки: зі словниками мов і версією вмісту (BE-15). */
export type UnitDetail = components['schemas']['UnitDetail'];
export type UpdateUnitBody = components['schemas']['UpdateUnitRequest'];

/** Одиниця для форми правки; право `Uom.EditCatalog`. */
export function getUnit(unitId: number): Promise<UnitDetail> {
  return apiFetch<UnitDetail>(`/api/v1/units/${unitId}`);
}

/**
 * Змінює позначення, назву і — поки на одиницю ніщо не посилається — множник
 * і зсув.
 *
 * ⛔ `If-Match` несе `rowVersion` тієї версії, яку ПОКАЗАЛИ людині (той самий
 * контракт, що `updateDataSource`). Чужа версія — `409 unitChanged`.
 */
export function updateUnit(unitId: number, body: UpdateUnitBody, rowVersion: string): Promise<UnitDetail> {
  return apiFetch<UnitDetail>(`/api/v1/units/${unitId}`, {
    method: 'PUT',
    headers: { 'If-Match': `"${rowVersion}"` },
    body: JSON.stringify(body),
  });
}

/**
 * Заводить нову похідну одиницю (UI-аудит, lane 4).
 *
 * ⛔ Доти жоден обліковий запис, включно з повноправним адміністратором, не
 * мав шляху додати одиницю виміру — той самий клас дефекту, що вже
 * виправлений для довідників (`Q-200`).
 */
export function createUnit(body: CreateUnitRequest): Promise<UnitRef> {
  return apiFetch<UnitRef>('/api/v1/units', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });
}
