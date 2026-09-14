import { apiFetch } from '@/api/client';
import type { CreateUnitRequest, UnitRef } from '@/api/types';

/*
 * ⛔ Адреса записана повністю, а не збирається з помічника — той самий
 * прийом, що й `features/registries/api.ts`: сторож
 * `Кожна_дія_сервера_має_споживача_в_інтерфейсі` шукає в коді клієнта
 * літерал `/api/v1/…` разом із методом поруч.
 */

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
