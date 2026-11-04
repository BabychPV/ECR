import { apiFetch } from '@/api/client';
import type { components } from '@/api/schema';

/** Ієрархія довідника: рівні (NAE plan N1). */
export type RegistryHierarchy = components['schemas']['RegistryHierarchyDto'];
export type RegistryLevel = components['schemas']['RegistryLevelDto'];
/** Дерево довідника: плаский список вузлів. */
export type RegistryTree = components['schemas']['RegistryTreeDto'];
export type RegistryTreeNode = components['schemas']['RegistryTreeNodeDto'];
type MoveBody = components['schemas']['MoveRegistryEntryRequest'];
type MoveResult = components['schemas']['MoveRegistryEntryResponse'];
type RetireBody = components['schemas']['RetireRegistryEntryRequest'];
type RetireResult = components['schemas']['RetireRegistryEntryResponse'];

/*
 * ⛔ Адреси записані повністю, а не збираються з помічника: сторож
 * `Кожна_дія_сервера_має_споживача_в_інтерфейсі` шукає літерал `/api/v1/…` разом із методом поруч.
 */

/** Рівні ієрархічного довідника. */
export function getRegistryHierarchy(code: string): Promise<RegistryHierarchy> {
  return apiFetch<RegistryHierarchy>(`/api/v1/registries/${encodeURIComponent(code)}/hierarchy`);
}

/**
 * Дерево пласким списком (клієнт збирає за `parentEntryId`).
 *
 * ⚠ `q` порожній - параметр не надсилається: сервер без `q` віддає все дерево, а `q=` порожнім рядком - зайвий шум у журналі.
 */
export function getRegistryTree(code: string, includeRetired: boolean, q: string): Promise<RegistryTree> {
  const query = new URLSearchParams();
  if (includeRetired) query.set('includeRetired', 'true');
  if (q.trim() !== '') query.set('q', q.trim());
  const suffix = query.size > 0 ? `?${query.toString()}` : '';

  // ⚠ Суфікс дописується ПІСЛЯ літерала адреси: сторож шляхів читає шаблон як `…/tree{p}` і не знайшов би дію.
  return apiFetch<RegistryTree>(`/api/v1/registries/${encodeURIComponent(code)}/tree` + suffix);
}

/**
 * Перенесення вузла (новий батько і/або порядок серед сусідів).
 *
 * ⚠ Недозволене місце (порядок рівнів, цикл, глибина) - `422 ECR-REG-0422`.
 */
export function moveRegistryEntry(code: string, id: number, body: MoveBody): Promise<MoveResult> {
  return apiFetch<MoveResult>(`/api/v1/registries/${encodeURIComponent(code)}/entries/${String(id)}/move`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });
}

/**
 * М'яке виведення вузла: вікно дійсності закривається, вузол не видаляється.
 *
 * ⚠ Вузол із живими дітьми без `cascade` - `409 ECR-REG-0409 nodeHasLiveChildren`.
 */
export function retireRegistryEntry(code: string, id: number, body: RetireBody): Promise<RetireResult> {
  return apiFetch<RetireResult>(`/api/v1/registries/${encodeURIComponent(code)}/entries/${String(id)}/retire`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });
}
