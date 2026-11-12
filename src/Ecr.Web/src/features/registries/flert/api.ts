import { apiFetch } from '@/api/client';
import type { components } from '@/api/schema';

/** Стан синку складу газу FLERT (D-476): чи увімкнено, де показувати, останній прогін. */
export type FlertSyncStatus = components['schemas']['FlertSyncStatusDto'];

/** Один прогін синку FLERT. */
export type FlertSyncRun = components['schemas']['FlertSyncRunDto'];

/** `202`: задача синку в черзі; стан — `GET /api/v1/jobs/{jobId}`. */
export type FlertSyncAccepted = components['schemas']['JobAcceptedResponse'];

/*
 * ⛔ Адреса записана повністю, а не збирається з помічника: сторож
 * `Кожна_дія_сервера_має_споживача_в_інтерфейсі` шукає літерал `/api/v1/…`.
 */

/** Стан синку (`Integration.View` або `Integration.Manage`). */
export function flertSyncStatus(): Promise<FlertSyncStatus> {
  return apiFetch<FlertSyncStatus>('/api/v1/integration/flert-sync/status');
}

/**
 * «Синхронізувати зараз» (`Integration.Manage`). Поки попередній синк у черзі — той самий `jobId` (злиття);
 * «готово» задачі означає, що прогін записано в журнал, а не обов'язково, що копію змінено.
 */
export function syncFlertNow(): Promise<FlertSyncAccepted> {
  return apiFetch<FlertSyncAccepted>('/api/v1/integration/flert-sync', { method: 'POST' });
}
