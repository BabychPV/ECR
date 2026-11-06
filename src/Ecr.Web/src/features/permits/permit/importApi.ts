import { apiFetch } from '@/api/client';
import type { components } from '@/api/schema';

type Schemas = components['schemas'];

/** Збережене зіставлення стовпців таблиці (спільне) і її стовпці НАЕ / Region-Area, які виводяться з джерела. */
export type ImportMapping = Schemas['PermitImportMappingDto'];
export type LookupResult = Schemas['PermitImportLookupResultDto'];
export type LookupValue = Schemas['PermitImportLookupValueDto'];
export type DerivedSource = Schemas['PermitImportDerivedDto'];
export type DerivedCell = Schemas['PermitImportDerivedCellDto'];
export type ImportSheet = Schemas['PermitImportSheetDto'];

/*
 * ⛔ Адреса кожної дії - літерал `/api/v1/…` (сторож `Кожна_адреса_яку_викликає_клієнт_існує_на_сервері`).
 */

/** Збережене зіставлення таблиці (порожнє, якщо його немає). Право `Registry.View` або грант `Read`. */
export function getImportMapping(registryCode: string): Promise<ImportMapping> {
  return apiFetch<ImportMapping>(`/api/v1/permit-tables/${encodeURIComponent(registryCode)}/import-mapping`);
}

/**
 * Зберігає зіставлення (спільне для всіх). Наявне - з `If-Match` = `rowVersion` останнього читання (чужа зміна - `409`).
 * Право `Permit.EditType`.
 */
export function saveImportMapping(
  registryCode: string,
  columns: Readonly<Record<string, string>>,
  rowVersion: string | null,
): Promise<ImportMapping> {
  const headers: Record<string, string> = { 'Content-Type': 'application/json' };
  if (rowVersion !== null) headers['If-Match'] = `"${rowVersion}"`;
  return apiFetch<ImportMapping>(`/api/v1/permit-tables/${encodeURIComponent(registryCode)}/import-mapping`, {
    method: 'PUT',
    headers,
    body: JSON.stringify({ columns }),
  });
}

/** Значення стовпців-посилань → записи (за кодом чи назвою) і Region/Area джерел НАЕ. Нічого не пишеться. */
export function resolveImportLookups(
  registryCode: string,
  columns: readonly { readonly column: string; readonly values: readonly string[] }[],
): Promise<LookupResult> {
  return apiFetch<LookupResult>(`/api/v1/permit-tables/${encodeURIComponent(registryCode)}/import/lookups`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ columns, derive: true }),
  });
}

/**
 * Аркуш `.xlsx` текстом (сервер; бібліотеки книг у клієнті немає). `sheet` порожній - перший видимий. `Content-Type` не ставиться:
 * межу multipart генерує браузер. Право `Registry.EditData` або грант `Write`.
 */
export function readImportWorkbook(registryCode: string, file: File, sheet: string | null): Promise<ImportSheet> {
  const form = new FormData();
  form.append('file', file);
  if (sheet !== null) form.append('sheet', sheet);
  return apiFetch<ImportSheet>(`/api/v1/permit-tables/${encodeURIComponent(registryCode)}/import/workbook`, {
    method: 'POST',
    body: form,
  });
}

/** Чи файл - книга Excel (`.xlsx`), а не текст із роздільниками. */
export function isWorkbook(file: File): boolean {
  return /\.xlsx$/i.test(file.name) || file.type === 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet';
}
