import type { QueryClient } from '@tanstack/react-query';
import { apiFetch, EcrApiError } from '@/api/client';
import type { components } from '@/api/schema';

/**
 * ✎ DOCUX L6-клієнт (контракт doc-ux-server): порожні обов'язкові входи методологій таблиці — з СЕРВЕРА, по ВСІХ рядках
 * (і тих, яких людина не правила), і для таблиць, ще не змонтованих прокруткою. Модель сітки (`needsInput.ts`) лишається
 * для миттєвого відгуку під час введення; цей перелік — правда для лічильника, F8 і «Submit».
 */
export type RequiredInputs = components['schemas']['RequiredInputsDto'];
export type RequiredInputCellDto = components['schemas']['RequiredInputCellDto'];

export const requiredInputsKey = (documentId: number, tableInstanceId: number) =>
  ['required-inputs', documentId, tableInstanceId] as const;

/** `GET /documents/{id}/tables/{tableInstanceId}/required-inputs`. */
export function getRequiredInputs(documentId: number, tableInstanceId: number): Promise<RequiredInputs> {
  return apiFetch<RequiredInputs>(`/api/v1/documents/${String(documentId)}/tables/${String(tableInstanceId)}/required-inputs`);
}

/**
 * Після збереження рядків: перечитати перелік таблиці, якщо збережений рядок у ньому є (заповнили вимогу). Інші
 * збереження запиту не дають — автозбереження не платить переліком за кожну правку.
 */
export function refreshRequiredInputsAfterSave(
  queryClient: QueryClient,
  documentId: number,
  tableInstanceId: number,
  rowKeys: readonly string[],
): void {
  const key = requiredInputsKey(documentId, tableInstanceId);
  const cached = queryClient.getQueryData<RequiredInputs>(key);
  if (cached === undefined) return;
  const saved = new Set(rowKeys);
  if (cached.cells.some((cell) => saved.has(cell.rowKey))) void queryClient.invalidateQueries({ queryKey: key, exact: true });
}

/** Відмова подання через порожні обов'язкові входи (`ECR-SUB-4221` validationBlocked / `ECR-CALC-0437`). */
export function isRequiredInputsRefusal(error: unknown): boolean {
  if (!(error instanceof EcrApiError)) return false;
  const code = error.problem.errorCode;
  const messageKey = error.problem.extensions2?.['messageKey'];

  return code === 'ECR-CALC-0437' || (code === 'ECR-SUB-4221' && typeof messageKey === 'string' && messageKey.includes('validationBlocked'));
}

/** Перечитати переліки всіх таблиць документа (після відмови сервера в поданні). */
export function refreshAllRequiredInputs(queryClient: QueryClient, documentId: number): Promise<void> {
  return queryClient.invalidateQueries({ queryKey: ['required-inputs', documentId] });
}
