import { apiFetch } from '@/api/client';
import type { components } from '@/api/schema';

/** Стан «закритий місяць відкрито лише для цього документа» (приймальна 08.11 (i)). */
export type DocumentPeriodReopenState = components['schemas']['DocumentPeriodReopenState'];

/** Діючий виняток документа за місяць. */
export type DocumentPeriodReopenView = components['schemas']['DocumentPeriodReopenView'];

/** Тіло запиту на відкриття. */
export type OpenDocumentPeriodReopenRequest = components['schemas']['OpenDocumentPeriodReopenRequest'];

/**
 * Ключ кешу стану — під префіксом документа (`queryKeys.document.ofDocument`), тож будь-яка інвалідація документа
 * (подання, імпорт, відкриття) зачіпає й його. Третій елемент — рядок, щоб не збігтися з `['document', id, period]`.
 */
export function periodReopenKey(documentId: number, periodKey: number): readonly unknown[] {
  return ['document', documentId, 'period-reopen', periodKey];
}

/*
 * ⛔ Адреси записані ПОВНІСТЮ поруч із `apiFetch` (сторож `EndpointCoverageTests` шукає літерал `/api/v1/…`).
 */

/** `GET /api/v1/documents/{id}/period-reopen?periodKey=` — стан місяця для документа. */
export function getDocumentPeriodReopen(documentId: number, periodKey: number): Promise<DocumentPeriodReopenState> {
  return apiFetch<DocumentPeriodReopenState>(
    `/api/v1/documents/${String(documentId)}/period-reopen?periodKey=${String(periodKey)}`,
  );
}

/** `POST /api/v1/documents/{id}/period-reopen` — відкриває закритий місяць лише для цього документа. */
export function postDocumentPeriodReopen(
  documentId: number,
  body: OpenDocumentPeriodReopenRequest,
): Promise<DocumentPeriodReopenView> {
  return apiFetch<DocumentPeriodReopenView>(`/api/v1/documents/${String(documentId)}/period-reopen`, {
    method: 'POST',
    body: JSON.stringify(body),
  });
}

/** Діючі винятки видимих документів проєкту і чи може викликач їх закрити (вкладка Periods, приймальна i4). */
export type ProjectDocumentReopens = components['schemas']['ProjectDocumentReopens'];

/** Один діючий виняток документа проєкту. */
export type ProjectDocumentReopenItem = components['schemas']['ProjectDocumentReopenItem'];

/** Ключ кешу переліку винятків проєкту — поруч із календарем `['periods', projectId]`. */
export function projectDocumentReopensKey(projectId: number): readonly unknown[] {
  return ['periods', projectId, 'document-reopens'];
}

/** `GET /api/v1/projects/{id}/document-reopens` — діючі винятки видимих документів проєкту. */
export function getProjectDocumentReopens(projectId: number): Promise<ProjectDocumentReopens> {
  return apiFetch<ProjectDocumentReopens>(`/api/v1/projects/${String(projectId)}/document-reopens`);
}

/** `DELETE /api/v1/documents/{id}/period-reopen?periodKey=` — «Close now»: закриває виняток до строку. */
export function closeDocumentPeriodReopen(documentId: number, periodKey: number): Promise<void> {
  return apiFetch<void>(`/api/v1/documents/${String(documentId)}/period-reopen?periodKey=${String(periodKey)}`, {
    method: 'DELETE',
  });
}
