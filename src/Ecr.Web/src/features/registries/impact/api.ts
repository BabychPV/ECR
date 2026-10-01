import { apiFetch } from '@/api/client';
import type { components } from '@/api/schema';

/** Документи відкритих періодів, зачеплені правкою довідника (`GET …/impact`, RT-25). */
export type RegistryImpactResponse = components['schemas']['RegistryImpactResponse'];

/** Один зачеплений документ: період, його стан і методології, через які він залежить. */
export type RegistryImpactItem = components['schemas']['RegistryImpactItemDto'];

/*
 * ⛔ Адреса записана повністю, а не збирається з помічника: сторож
 * `Кожна_дія_сервера_має_споживача_в_інтерфейсі` шукає літерал `/api/v1/…`.
 */

/**
 * Які документи ВІДКРИТИХ періодів залежать від довідника (`Open`/`Grace`).
 *
 * ⛔ Закриті періоди сервер не повертає взагалі. Перерахунок цей виклик не ставить: це лише
 * читання, дію (`recalculate-impacted`) людина вмикає окремо.
 *
 * ⚠ `total` — кількість зачеплених документів, `items` обрізаний сторінкою; `truncated` — вибірка
 * впертась у стелю сервера, тож справжня кількість більша за `total`.
 */
export function registryImpact(code: string): Promise<RegistryImpactResponse> {
  return apiFetch<RegistryImpactResponse>(`/api/v1/registries/${encodeURIComponent(code)}/impact`);
}

/** Запуск перерахунку зачеплених: документи з `impact` (`null` — усі доступні) і обов'язкова причина. */
export type RecalculateImpactedRequest = components['schemas']['RecalculateImpactedRequest'];

/** `202`: один `jobId` на весь набір; стан — `GET /api/v1/jobs/{jobId}`. */
export type RecalculateImpactedAccepted = components['schemas']['JobAcceptedResponse'];

/**
 * Ставить перерахунок зачеплених документів у чергу (`POST …/recalculate-impacted`, RT-25).
 *
 * ⛔ Перерахунок ніколи не автоматичний (`R-14`): виклик — дія людини над переліком `registryImpact`.
 * Закриті періоди сервер відмовляє `422`; «готово» задачі означає «поставлено», не «перераховано».
 */
export function recalculateImpacted(
  code: string,
  body: RecalculateImpactedRequest,
): Promise<RecalculateImpactedAccepted> {
  return apiFetch<RecalculateImpactedAccepted>(
    `/api/v1/registries/${encodeURIComponent(code)}/recalculate-impacted`,
    {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    },
  );
}