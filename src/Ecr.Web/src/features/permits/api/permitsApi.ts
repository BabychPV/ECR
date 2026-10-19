import { apiFetch } from '@/api/client';
import type { components } from '@/api/schema';

import type {
  CreatePermitRequest,
  EndPermitRequest,
  Permit,
  PermitListParams,
  RenewPermitRequest,
  UpdatePermitRequest,
} from './types';

/*
 * ⛔ Адреса кожної дії записана повністю, а не збирається з помічника: сторож
 * `Кожна_дія_сервера_має_споживача_в_інтерфейсі` шукає літерал `/api/v1/…` разом із методом поруч
 * (Permits, PM-04; інтерфейсу ще немає — його додає PM-07).
 *
 * Рядки таблиць дозволу тут НЕ читаються й не пишуться: для них є загальні `/registries/{PRM_…}/rows?parentEntryId=`
 * (код реєстру — `Permit.tables[].registryCode`) і `entries/batch`. Закінчений дозвіл у сітці лише для читання — це
 * правило інтерфейсу, сервер записів у таблиці закінченого дозволу не блокує.
 */

type PermitPage = components['schemas']['PagedResultOfPermitListItemDto'];

/** Перелік дозволів усіх доступних типів. Право `Registry.View`. `422 ECR-PRM-0422`, якщо стан чи курсор хибні. */
export function listPermits(params: PermitListParams = {}): Promise<PermitPage> {
  const query = new URLSearchParams();
  if (params.type) query.set('type', params.type);
  if (params.status) query.set('status', params.status);
  if (params.q) query.set('q', params.q);
  if (params.asOf) query.set('asOf', params.asOf);
  if (params.limit) query.set('limit', String(params.limit));
  if (params.cursor) query.set('cursor', params.cursor);
  const path = '/api/v1/permits';

  return apiFetch<PermitPage>(query.size > 0 ? `${path}?${query.toString()}` : path);
}

/** Дозвіл: шапка, стан на `asOf` (типово сьогодні), таблиці з підсумками. `404 ECR-PRM-0404`, якщо його немає. */
export function getPermit(id: number, asOf?: string): Promise<Permit> {
  const path = `/api/v1/permits/${id}`;

  return apiFetch<Permit>(asOf ? `${path}?asOf=${encodeURIComponent(asOf)}` : path);
}

/**
 * Заводить дозвіл ЧЕРНЕТКОЮ за опублікованим типом. `409 ECR-PRM-4095` — такий номер з таким початком уже є; `422
 * ECR-PRM-0422` — номер, дати, тип не опублікований. Право `Registry.EditData`.
 */
export function createPermit(body: CreatePermitRequest): Promise<Permit> {
  return apiFetch<Permit>('/api/v1/permits', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });
}

/**
 * Змінює шапку чернетки чи чинного дозволу. `409 ECR-PRM-4096` — токен застарів (`changed`) чи дозвіл закінчився
 * (`notEditable`); `409 ECR-PRM-4092` — перетин із іншим чинним дозволом того самого номера.
 */
export function updatePermit(id: number, body: UpdatePermitRequest): Promise<Permit> {
  return apiFetch<Permit>(`/api/v1/permits/${id}`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });
}

/** Видаляє чернетку разом із рядками її таблиць. `409 ECR-PRM-4096` — не чернетка (`notDraft`) чи є посилання (`inUse`). */
export function deletePermit(id: number): Promise<void> {
  return apiFetch<void>(`/api/v1/permits/${id}`, { method: 'DELETE' });
}

/** Активує чернетку. `409 ECR-PRM-4092` — перетин із чинним дозволом того самого номера; `409 ECR-PRM-4096` — не чернетка. */
export function activatePermit(id: number): Promise<Permit> {
  return apiFetch<Permit>(`/api/v1/permits/${id}/activate`, { method: 'POST' });
}

/** Достроково завершує чинний дозвіл (причина ≥ 10 символів). `409 ECR-PRM-4096` (`notActive`), `422 ECR-PRM-0422`. */
export function endPermit(id: number, body: EndPermitRequest): Promise<Permit> {
  return apiFetch<Permit>(`/api/v1/permits/${id}/end`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });
}

/**
 * Поновлює дозвіл: повертає НОВУ чернетку (початок = кінець поточного, рядки скопійовано). `409 ECR-PRM-4096`
 * (`notRenewable` для чернетки), `409 ECR-PRM-4095` — вже поновлено.
 */
export function renewPermit(id: number, body?: RenewPermitRequest): Promise<Permit> {
  return apiFetch<Permit>(
    `/api/v1/permits/${id}/renew`,
    body
      ? { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) }
      : { method: 'POST' },
  );
}
