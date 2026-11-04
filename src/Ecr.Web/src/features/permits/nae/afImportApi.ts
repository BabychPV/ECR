import { apiFetch } from '@/api/client';
import type { components } from '@/api/schema';

export type AfImportRequest = components['schemas']['ImportNaeFromAfRequest'];
export type AfImportReport = components['schemas']['NaeAfImportReport'];
export type AfImportIssue = components['schemas']['NaeAfIssueDto'];

/**
 * Одноразове завантаження структури NAE з дерева PI AF (NAE plan N2). `dryRun` - лише звіт, нічого не пишеться
 * (навіть довідник); застосування створює довідник `NAE_TREE` і відсутні вузли, наявні не змінює.
 *
 * ⚠ Адреса записана повністю: сторож `Кожна_дія_сервера_має_споживача_в_інтерфейсі` шукає літерал разом із методом.
 */
export function importNaeFromAf(body: AfImportRequest): Promise<AfImportReport> {
  return apiFetch<AfImportReport>('/api/v1/nae-tree/af-import', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });
}
