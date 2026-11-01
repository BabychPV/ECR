import { apiFetch } from '@/api/client';
import type {
  AirAnalysis,
  AirImportOptions,
  AirImportResult,
  AirLimitsPermit,
  AirPreview,
  AirScopeCodes,
  ImportProfile,
  ImportProfileSummary,
  NaeTree,
  SaveImportProfileRequest,
} from './airTypes';

/*
 * ⛔ Адреса кожної дії записана повністю (літерал `/api/v1/…` з методом поруч): сторож
 * `Кожна_дія_сервера_має_споживача_в_інтерфейсі` шукає саме їх (NAE-план N3/N5).
 *
 * ⚠ Імпорт — `multipart/form-data` з двох частин: `file` (книга) і `request` (JSON `AirImportOptions`). `Content-Type`
 * не ставиться: межу multipart генерує браузер (`api/client.ts`). Ім'я частини `file` — як у контролері.
 */

/** Реєстр дерева НАЕ: цілі «створити під вузлом» у кроці розв'язання відсутніх НАЕ. */
export const NaeRegistryCode = 'NAE_TREE';

function importForm(file: File, options: AirImportOptions): FormData {
  const form = new FormData();
  form.append('file', file);
  form.append('request', JSON.stringify(options));

  return form;
}

/** Картка дозволу: джерела з речовинами та підсумки по речовинах. Право `Registry.View`. */
export function getAirLimits(permitId: number): Promise<AirLimitsPermit> {
  return apiFetch<AirLimitsPermit>(`/api/v1/permits/${permitId}/air-limits`);
}

/** Профілі імпорту таблиці джерел. Право `Registry.View`. */
export function listImportProfiles(): Promise<ImportProfileSummary[]> {
  return apiFetch<ImportProfileSummary[]>('/api/v1/air-limits/import-profiles');
}

/** Один профіль зі специфікацією. `404`, якщо його немає. */
export function getImportProfile(code: string): Promise<ImportProfile> {
  return apiFetch<ImportProfile>(`/api/v1/air-limits/import-profiles/${encodeURIComponent(code)}`);
}

/** Створює чи замінює профіль. Право `Registry.EditDefinition`; `409` — токен застарів, `422 regSpecInvalid` — хибна специфікація. */
export function saveImportProfile(code: string, body: SaveImportProfileRequest): Promise<ImportProfile> {
  return apiFetch<ImportProfile>(`/api/v1/air-limits/import-profiles/${encodeURIComponent(code)}`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });
}

/** Розкладка файлу за профілем чи незбереженою специфікацією; нічого не пишеться. Право `Registry.EditData`. */
export function analyseAirImport(file: File, options: AirImportOptions): Promise<AirAnalysis> {
  return apiFetch<AirAnalysis>('/api/v1/air-limits/import/analyse', { method: 'POST', body: importForm(file, options) });
}

/** Перегляд імпорту в дозвіл: помилки, відсутні НАЕ, обсяг заміни, diff, відбиток плану. Нічого не пишеться. */
export function previewAirImport(permitId: number, file: File, options: AirImportOptions): Promise<AirPreview> {
  return apiFetch<AirPreview>(`/api/v1/permits/${permitId}/air-limits/import/preview`, {
    method: 'POST',
    body: importForm(file, options),
  });
}

/**
 * Застосовує імпорт однією транзакцією («усе або нічого»). Потрібні `confirmedOtherOwners` (якщо замінюються чужі
 * дозволи) і `expectedFingerprint` з перегляду: інакше `422 regReplaceNotConfirmed` / `regPlanChanged`.
 */
export function applyAirImport(permitId: number, file: File, options: AirImportOptions): Promise<AirImportResult> {
  return apiFetch<AirImportResult>(`/api/v1/permits/${permitId}/air-limits/import/apply`, {
    method: 'POST',
    body: importForm(file, options),
  });
}

/** Плоске дерево НАЕ (вузли за `parentEntryId`); лише чинні вузли. */
export function getNaeTree(): Promise<NaeTree> {
  return apiFetch<NaeTree>(`/api/v1/registries/${encodeURIComponent(NaeRegistryCode)}/tree`);
}

/** Regions and areas of the NAE tree (with group names): the File step picks from them instead of free text. Registry.View. */
export function getAirScopeCodes(): Promise<AirScopeCodes> {
  return apiFetch<AirScopeCodes>('/api/v1/air-limits/scope-codes');
}
