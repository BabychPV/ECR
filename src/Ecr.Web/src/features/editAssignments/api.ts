import { useMutation, useQuery, useQueryClient, type UseQueryResult } from '@tanstack/react-query';
import { apiFetch, EcrApiError } from '@/api/client';
import { queryKeys } from '@/api/queryKeys';
import { invalidateSlices } from '@/features/grid/sliceCache';
import type {
  AssignmentSlice,
  Candidate,
  EditAssignment,
  EditAssignmentsList,
  EditorSet,
  MyEditAssignmentsView,
  UpsertAssignmentRequest,
} from './types';

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

/** Порожня відповідь: немає передач або сервер ще не вміє їх віддавати. */
const NoAssignments: MyEditAssignmentsView = { assignedToMe: [], awaitingMyValidation: [] };

/**
 * «Мої» передачі всіх документів: `{assignedToMe, awaitingMyValidation}` - єдина форма для «My tasks» (5c) і смуги
 * виконавця (5b; та фільтрує за `documentId` на клієнті).
 *
 * ⚠ `404`/`403` = «функції для цього користувача/стенда немає» (сервер EA-4 ще не злито, або передачі вимкнено):
 * це порожній стан, а не помилка. Без цього кожен відкритий шухляда «My tasks» писав би «не вдалося завантажити»
 * усім. Інші збої (5xx, мережа) лишаються помилкою.
 */
export async function getMyEditAssignments(): Promise<MyEditAssignmentsView> {
  try {
    // 204/пусте тіло дає `null`: звертання `dto.assignedToMe` кидало б TypeError замість порожнього стану.
    const dto = await apiFetch<Partial<MyEditAssignmentsView> | null>('/api/v1/me/edit-assignments');

    return { assignedToMe: dto?.assignedToMe ?? [], awaitingMyValidation: dto?.awaitingMyValidation ?? [] };
  } catch (error) {
    if (error instanceof EcrApiError && (error.problem.status === 404 || error.problem.status === 403)) return NoAssignments;

    throw error;
  }
}

/** Опитування, поки шухляда «My tasks» відкрита, - раз на хвилину; закрита - лише при фокусі/навігації. */
const OpenedPollMs = 60_000;

/**
 * Власні передачі (один ключ кешу `editAssignments.mine()` на шухляду й смугу: один запит, а не два).
 * Стан змінює інша людина (власник повертає чи валідує): без перезапиту на фокус смуга брехала б до F5.
 */
export function useMyEditAssignments(opened = false): UseQueryResult<MyEditAssignmentsView> {
  return useQuery({
    queryKey: queryKeys.editAssignments.mine(),
    queryFn: getMyEditAssignments,
    refetchOnWindowFocus: true,
    refetchInterval: opened ? OpenedPollMs : false,
    staleTime: opened ? 0 : 30_000,
  });
}

/** `POST …/{id}/fill`: лише сам виконавець, ідемпотентно (дизайн §6). */
export function fillEditAssignment(documentId: number, id: number): Promise<unknown> {
  return apiFetch<unknown>(`${base(documentId)}/${String(id)}/fill`, json('POST', {}));
}

/**
 * Галочка «Заповнено». Після успіху перечитуються власні передачі й зрізи документа: `cellPermissions` стали
 * `AssignmentFilled`. ⚠ `meta.handled`: помилку показує виклик (`mutate({onError})`), друга від сітки не потрібна.
 */
export function useFillEditAssignment(documentId: number, periodKey: number) {
  const queryClient = useQueryClient();

  return useMutation({
    meta: { handled: true },
    mutationFn: (assignmentId: number) => fillEditAssignment(documentId, assignmentId),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: queryKeys.editAssignments.mine() });
      await invalidateSlices(queryClient, { documentId, periodKey });
    },
  });
}
