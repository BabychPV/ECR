import { apiFetch } from '@/api/client';
import type { RenameSheetGroupRequest, SheetCopyCreated } from '@/api/types';

/**
 * Звернення до копій аркушів документа (типи — із `schema.d.ts`).
 *
 * ⚠ Адреси записані ПОВНІСТЮ поруч із викликом — так їх шукає сторож споживачів дій сервера.
 */

/** Створює копію аркуша; повертає лише Id копії (`sheetCopyId`) і її номер у групі — таблиці перечитуються. */
export function createSheetCopy(documentId: number, sheetDefId: number): Promise<SheetCopyCreated> {
  return apiFetch<SheetCopyCreated>(
    `/api/v1/documents/${String(documentId)}/sheets/${String(sheetDefId)}/copies`,
    { method: 'POST' },
  );
}

/** Перейменовує групу аркушів (оригінал і всі копії). */
export function renameSheetGroup(
  documentId: number,
  sheetDefId: number,
  request: RenameSheetGroupRequest,
): Promise<void> {
  return apiFetch<void>(`/api/v1/documents/${String(documentId)}/sheets/${String(sheetDefId)}/name`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(request),
  });
}

/** Видаляє копію; сервер відмовляє (409) для оригіналу й поданої копії. */
export function deleteSheetCopy(documentId: number, sheetCopyId: number): Promise<void> {
  return apiFetch<void>(`/api/v1/documents/${String(documentId)}/sheet-copies/${String(sheetCopyId)}`, {
    method: 'DELETE',
  });
}
