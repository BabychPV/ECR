import { apiFetch, apiFetchResponse } from '@/api/client';
import type { components } from '@/api/schema';

export type CreateNaeRootRequest = components['schemas']['CreateNaeRootRequest'];
export type NaeRootResult = components['schemas']['NaeRootResultDto'];
export type NaeSeedRootsResult = components['schemas']['NaeSeedRootsResultDto'];
/** Частина форми `request` (JSON-рядок, тому в схемі OpenAPI її немає): серверний `NaeTreeImportOptions`. */
export type NaeTreeImportOptions = { expectedFingerprint?: string | null };
export type NaeTreeImportReport = components['schemas']['NaeTreeImportReportDto'];
export type NaeTreeImportIssue = components['schemas']['NaeTreeImportIssueDto'];

/**
 * Структура NAE без AF: перший вузол (регіон, опційно район), корені з `LAND_REGION`, імпорт дерева з Excel
 * (аналіз → перегляд → застосування, все або нічого) і порожній шаблон.
 *
 * ⚠ Адреси записані повністю, метод названо явно: сторож `Кожна_дія_сервера_має_споживача_в_інтерфейсі` шукає літерал
 * разом із методом. Заголовок `Content-Type` для multipart не ставиться: межу генерує браузер (`api/client.ts`).
 */
export function createNaeRoot(body: CreateNaeRootRequest): Promise<NaeRootResult> {
  return apiFetch<NaeRootResult>('/api/v1/nae-tree/roots', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });
}

/** Корені-регіони з довідника `LAND_REGION`; лише для порожнього дерева (інакше 422 `naeTreeNotEmpty`). */
export function seedNaeRoots(): Promise<NaeSeedRootsResult> {
  return apiFetch<NaeSeedRootsResult>('/api/v1/nae-tree/seed-roots', { method: 'POST' });
}

/** Порожній шаблон імпорту (`.xlsx`). */
export async function fetchNaeTreeTemplate(): Promise<Blob> {
  const response = await apiFetchResponse('/api/v1/nae-tree/import/template', { method: 'GET' });
  return response.blob();
}

function importForm(file: File, options?: NaeTreeImportOptions): FormData {
  const form = new FormData();
  form.append('file', file);
  if (options) form.append('request', JSON.stringify(options));

  return form;
}

/** Розбір файлу без бази: рядки, вузли до створення, помилки файлу. */
export function analyseNaeTreeImport(file: File): Promise<NaeTreeImportReport> {
  return apiFetch<NaeTreeImportReport>('/api/v1/nae-tree/import/analyse', { method: 'POST', body: importForm(file) });
}

/** Перегляд над збереженим деревом: нові, наявні (без змін), конфлікти за бізнес-ключем. Нічого не пишеться. */
export function previewNaeTreeImport(file: File): Promise<NaeTreeImportReport> {
  return apiFetch<NaeTreeImportReport>('/api/v1/nae-tree/import/preview', { method: 'POST', body: importForm(file, {}) });
}

/** Застосування однією транзакцією; `expectedFingerprint` — відбиток із перегляду. */
export function applyNaeTreeImport(file: File, expectedFingerprint: string): Promise<NaeTreeImportReport> {
  return apiFetch<NaeTreeImportReport>('/api/v1/nae-tree/import/apply', {
    method: 'POST',
    body: importForm(file, { expectedFingerprint }),
  });
}
