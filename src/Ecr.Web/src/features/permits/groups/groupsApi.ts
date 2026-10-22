import { apiFetch } from '@/api/client';
import type { components } from '@/api/schema';

/*
 * ⛔ Адреса кожної дії записана повністю (літерал `/api/v1/…` разом із методом поруч): так її читає сторож
 * `Кожна_дія_сервера_має_споживача_в_інтерфейсі` (Permits, групи типів, PRMG).
 */

export type PermitGroupDto = components['schemas']['PermitGroupDto'];
export type PermitGroupTypeDto = components['schemas']['PermitGroupTypeDto'];
export type CreatePermitGroupRequest = components['schemas']['CreatePermitGroupRequest'];
export type UpdatePermitGroupRequest = components['schemas']['UpdatePermitGroupRequest'];
export type ReplacePermitGroupTypesRequest = components['schemas']['ReplacePermitGroupTypesRequest'];

const Json = { 'Content-Type': 'application/json' } as const;

/** Усі групи типів дозволів (код, назви й опис мовами, склад, активність, використання). Право `Registry.View`. */
export function listPermitGroups(): Promise<PermitGroupDto[]> {
  return apiFetch<PermitGroupDto[]>('/api/v1/permit-groups');
}

/** Група за кодом з токеном `rowVersion`. `404 ECR-PRM-0405`, якщо групи немає. Право `Registry.View`. */
export function getPermitGroup(code: string): Promise<PermitGroupDto> {
  return apiFetch<PermitGroupDto>(`/api/v1/permit-groups/${encodeURIComponent(code)}`);
}

/**
 * Створює групу (і її прихований реєстр `PRMG_<код>`). `409 ECR-PRM-4097` - код (чи код реєстру) зайнятий;
 * `422 ECR-PRM-0422` - `messageKey` каже випадок (`permitGroupNameRequired`, `permitGroupCodeInvalid`, …).
 * Право `Permit.EditType`.
 */
export function createPermitGroup(body: CreatePermitGroupRequest): Promise<PermitGroupDto> {
  return apiFetch<PermitGroupDto>('/api/v1/permit-groups', {
    method: 'POST',
    headers: Json,
    body: JSON.stringify(body),
  });
}

/** Назва, опис і активність групи (деактивація - `isActive: false`). `409 ECR-PRM-4099` - токен застарів. */
export function updatePermitGroup(code: string, body: UpdatePermitGroupRequest): Promise<PermitGroupDto> {
  return apiFetch<PermitGroupDto>(`/api/v1/permit-groups/${encodeURIComponent(code)}`, {
    method: 'PUT',
    headers: Json,
    body: JSON.stringify(body),
  });
}

/** Замінює склад групи цілком (опубліковані типи в порядку показу). `409 ECR-PRM-4099`, `422 ECR-PRM-0422`. */
export function replacePermitGroupTypes(code: string, body: ReplacePermitGroupTypesRequest): Promise<PermitGroupDto> {
  return apiFetch<PermitGroupDto>(`/api/v1/permit-groups/${encodeURIComponent(code)}/types`, {
    method: 'PUT',
    headers: Json,
    body: JSON.stringify(body),
  });
}

/** Видаляє групу, яку не використовують шаблони. `409 ECR-PRM-4098` - використовується (лише деактивувати). */
export function deletePermitGroup(code: string): Promise<void> {
  return apiFetch<void>(`/api/v1/permit-groups/${encodeURIComponent(code)}`, { method: 'DELETE' });
}
