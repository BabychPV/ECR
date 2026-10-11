import { apiFetch } from '@/api/client';
import type {
  AssignmentSlice,
  Candidate,
  EditAssignment,
  EditAssignmentsList,
  EditorSet,
  UpsertAssignmentRequest,
} from './types';

/** Спільний префікс ключів кешу: будь-яка мутація інвалідує все під ним (шторка, «Мої задачі», смуга виконавця). */
export const editAssignmentsKey = ['edit-assignments'] as const;

export const documentAssignmentsKey = (documentId: number) => [...editAssignmentsKey, 'document', documentId] as const;
export const editorSetsKey = [...editAssignmentsKey, 'sets'] as const;
export const candidatesKey = (documentId: number, q: string) =>
  [...editAssignmentsKey, 'candidates', documentId, q] as const;

/** Найкоротший запит до `candidates` (дизайн §6: `q` ≥ 2). */
export const MinCandidateQuery = 2;

const base = (documentId: number): string => `/api/v1/documents/${String(documentId)}/edit-assignments`;

const json = (method: string, body: unknown): RequestInit => ({ method, body: JSON.stringify(body) });

export function getEditAssignments(documentId: number): Promise<EditAssignmentsList> {
  return apiFetch<EditAssignmentsList>(base(documentId));
}

/** Ідемпотентний upsert за ключем зрізу; для наявної передачі потрібна `baseVersion`. */
export function putEditAssignment(documentId: number, request: UpsertAssignmentRequest): Promise<EditAssignment> {
  return apiFetch<EditAssignment>(base(documentId), json('PUT', request));
}

export function deleteEditAssignment(documentId: number, id: number): Promise<void> {
  return apiFetch<void>(`${base(documentId)}/${String(id)}`, { method: 'DELETE' });
}

export function validateEditAssignment(documentId: number, id: number, baseVersion: string): Promise<EditAssignment> {
  return apiFetch<EditAssignment>(`${base(documentId)}/${String(id)}/validate`, json('POST', { baseVersion }));
}

export function returnEditAssignment(
  documentId: number,
  id: number,
  body: { readonly reason: string; readonly baseVersion: string; readonly userIds?: readonly number[] },
): Promise<EditAssignment> {
  return apiFetch<EditAssignment>(`${base(documentId)}/${String(id)}/return`, json('POST', body));
}

/** «Повторити для наступного місяця»: склад копіюється зі станами `Assigned`. */
export function copyEditAssignment(
  documentId: number,
  body: { readonly fromSlice: AssignmentSlice; readonly toSlices: readonly AssignmentSlice[] },
): Promise<unknown> {
  return apiFetch<unknown>(`${base(documentId)}/copy`, json('POST', body));
}

export function getCandidates(documentId: number, q: string): Promise<readonly Candidate[]> {
  return apiFetch<readonly Candidate[]>(`${base(documentId)}/candidates?q=${encodeURIComponent(q)}`);
}

export function getEditorSets(): Promise<readonly EditorSet[]> {
  return apiFetch<readonly EditorSet[]>('/api/v1/me/editor-sets');
}

export function createEditorSet(body: { readonly name: string; readonly userIds: readonly number[] }): Promise<EditorSet> {
  return apiFetch<EditorSet>('/api/v1/me/editor-sets', json('POST', body));
}
