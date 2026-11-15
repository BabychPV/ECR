import { apiFetch } from '@/api/client';
import type { components } from '@/api/schema';

/**
 * ✎ B75: блокування рядків документа й режим погодження версії шаблону — модуль API (контракт lane 1). Екрани
 * (діалог «Заблокувати рядки…», значок рядка, перелік блокувань, приховування подання) — окрема клієнтська lane.
 */

/** Блокування рядка (дати — часовий пояс проєкту, кінцева включно). */
export type RowLockDto = components['schemas']['RowLockDto'];

/** Тіло запиту на блокування рядків. */
export type CreateRowLocksRequest = components['schemas']['CreateRowLocksRequest'];

/** Режим погодження версії у відповіді. */
export type TemplateWorkflowModeDto = components['schemas']['TemplateWorkflowModeDto'];

/** Режим погодження: з поданням і затвердженням або без них. */
export type WorkflowMode = components['schemas']['SetTemplateWorkflowModeRequest']['workflowMode'];

/** Ключ кешу блокувань — під префіксом документа (будь-яка інвалідація документа зачіпає й його). */
export function rowLocksKey(documentId: number, periodKey: number): readonly unknown[] {
  return ['document', documentId, 'row-locks', periodKey];
}

/*
 * ⛔ Адреси записані ПОВНІСТЮ поруч із `apiFetch` (сторож `EndpointCoverageTests` шукає літерал `/api/v1/…`).
 */

/** `GET /api/v1/documents/{id}/row-locks?periodKey=` — незняті й не прострочені блокування документа за період. */
export function getRowLocks(documentId: number, periodKey: number): Promise<RowLockDto[]> {
  return apiFetch<RowLockDto[]>(
    `/api/v1/documents/${String(documentId)}/row-locks?periodKey=${String(periodKey)}`,
  );
}

/** `POST /api/v1/documents/{id}/row-locks` — блокує рядки таблиці (усе або нічого). */
export function postRowLocks(documentId: number, body: CreateRowLocksRequest): Promise<RowLockDto[]> {
  return apiFetch<RowLockDto[]>(`/api/v1/documents/${String(documentId)}/row-locks`, {
    method: 'POST',
    body: JSON.stringify(body),
  });
}

/** `DELETE /api/v1/documents/{id}/row-locks/{lockId}` — знімає блокування. */
export function deleteRowLock(documentId: number, lockId: number): Promise<void> {
  return apiFetch<void>(`/api/v1/documents/${String(documentId)}/row-locks/${String(lockId)}`, {
    method: 'DELETE',
  });
}

/** `PUT /api/v1/template-versions/{id}/workflow-mode` — режим погодження версії-чернетки. */
export function putTemplateWorkflowMode(versionId: number, workflowMode: WorkflowMode): Promise<TemplateWorkflowModeDto> {
  return apiFetch<TemplateWorkflowModeDto>(`/api/v1/template-versions/${String(versionId)}/workflow-mode`, {
    method: 'PUT',
    body: JSON.stringify({ workflowMode }),
  });
}
