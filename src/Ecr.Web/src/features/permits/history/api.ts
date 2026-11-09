import { apiFetch } from '@/api/client';
import type { components } from '@/api/schema';

/**
 * Feature (j), human 08.11: a permit changes mid-month — the row (or the document header) keeps its permit history
 * «KZ…A until 14.02 · KZ…B from 15.02» in the same document; the shared month is split by the user (P3 builds the UI on
 * these calls).
 */
export type DocumentPermitHistory = components['schemas']['DocumentPermitHistoryDto'];

/** Permit history of one row's permit cell, with the months still to split. */
export type RowPermitHistory = components['schemas']['RowPermitHistoryDto'];

/** Permit history of one header field. */
export type HeaderPermitHistory = components['schemas']['HeaderPermitHistoryDto'];

/** Body: add a permit to a row from a date (j1). */
export type AddRowPermitRequest = components['schemas']['AddRowPermitRequest'];

/** Body: undo the latest permit added to a row. */
export type UndoRowPermitRequest = components['schemas']['UndoRowPermitRequest'];

/** Body: add a permit to a header field from a date (jh1; Ship 1 — the 1st of a month). */
export type AddHeaderPermitRequest = components['schemas']['AddHeaderPermitRequest'];

/** Body: undo the latest permit added to a header field. */
export type UndoHeaderPermitRequest = components['schemas']['UndoHeaderPermitRequest'];

/**
 * Cache key — under the document prefix (`queryKeys.document.ofDocument`), so any invalidation of the document reaches it.
 */
export function permitHistoryKey(documentId: number, periodKey: number): readonly unknown[] {
  return ['document', documentId, 'permit-history', periodKey];
}

/*
 * ⛔ Addresses written IN FULL next to `apiFetch` (the `EndpointCoverageTests` guard looks for the `/api/v1/…` literal).
 */

/** `GET /api/v1/documents/{id}/permit-history?periodKey=` — the document's permit history and cells needing a split. */
export function getPermitHistory(documentId: number, periodKey: number): Promise<DocumentPermitHistory> {
  return apiFetch<DocumentPermitHistory>(
    `/api/v1/documents/${String(documentId)}/permit-history?periodKey=${String(periodKey)}`,
  );
}

/** `POST /api/v1/documents/{id}/permit-history/rows` — adds a permit to a row from a date. */
export function addRowPermit(documentId: number, body: AddRowPermitRequest): Promise<RowPermitHistory> {
  return apiFetch<RowPermitHistory>(`/api/v1/documents/${String(documentId)}/permit-history/rows`, {
    method: 'POST',
    body: JSON.stringify(body),
  });
}

/** `POST /api/v1/documents/{id}/permit-history/rows/undo` — undoes the latest permit added to a row. */
export function undoRowPermit(documentId: number, body: UndoRowPermitRequest): Promise<RowPermitHistory> {
  return apiFetch<RowPermitHistory>(`/api/v1/documents/${String(documentId)}/permit-history/rows/undo`, {
    method: 'POST',
    body: JSON.stringify(body),
  });
}

/** `POST /api/v1/documents/{id}/permit-history/header` — adds a permit to a header field from a date. */
export function addHeaderPermit(documentId: number, body: AddHeaderPermitRequest): Promise<HeaderPermitHistory> {
  return apiFetch<HeaderPermitHistory>(`/api/v1/documents/${String(documentId)}/permit-history/header`, {
    method: 'POST',
    body: JSON.stringify(body),
  });
}

/** `POST /api/v1/documents/{id}/permit-history/header/undo` — undoes the latest permit added to a header field. */
export function undoHeaderPermit(documentId: number, body: UndoHeaderPermitRequest): Promise<HeaderPermitHistory> {
  return apiFetch<HeaderPermitHistory>(`/api/v1/documents/${String(documentId)}/permit-history/header/undo`, {
    method: 'POST',
    body: JSON.stringify(body),
  });
}
