import { useMutation, useQuery, useQueryClient, type UseQueryResult } from '@tanstack/react-query';
import { apiFetch, EcrApiError } from '@/api/client';
import { queryKeys } from '@/api/queryKeys';
import { invalidateSlices } from '@/features/grid/sliceCache';
import type {
  ApplyEditorSetRequest,
  Candidate,
  CopyAssignmentRequest,
  EditAssignment,
  EditAssignmentBatch,
  EditAssignmentsList,
  EditorSet,
  MyEditAssignmentsView,
  ReturnAssignmentRequest,
  SaveEditorSetRequest,
  UpsertAssignmentRequest,
  ValidateAssignmentRequest,
} from './types';

/** Найкоротший запит до `candidates` (дизайн §6: `q` ≥ 2). */
export const MinCandidateQuery = 2;

/*
 * ⛔ Адреси - ЛІТЕРАЛИ в самому виклику (шаблонний рядок із `${…}`), а не `base(documentId)` + суфікс: сторож
 * `EndpointCoverageTests` читає код регулярним виразом і через допоміжну функцію не бачив жодного виклику (9 дій
 * сервера лишались «без споживача»). Метод - теж літерал `method: '…'` поруч з адресою. Відповідність схемі
 * (метод, шлях, тіло) стереже `__tests__/api.contract.test.ts`.
 */

export function getEditAssignments(documentId: number): Promise<EditAssignmentsList> {
  return apiFetch<EditAssignmentsList>(`/api/v1/documents/${String(documentId)}/edit-assignments`);
}

/** Ідемпотентний upsert за ключем зрізу (`POST`, не `PUT`); для наявної передачі потрібна `baseVersion`. */
export function upsertEditAssignment(documentId: number, request: UpsertAssignmentRequest): Promise<EditAssignment> {
  return apiFetch<EditAssignment>(`/api/v1/documents/${String(documentId)}/edit-assignments`, {
    method: 'POST',
    body: JSON.stringify(request),
  });
}

export function deleteEditAssignment(documentId: number, id: number): Promise<void> {
  return apiFetch<void>(`/api/v1/documents/${String(documentId)}/edit-assignments/${String(id)}`, { method: 'DELETE' });
}

export function validateEditAssignment(documentId: number, id: number, baseVersion: string): Promise<EditAssignment> {
  const body: ValidateAssignmentRequest = { baseVersion };

  return apiFetch<EditAssignment>(`/api/v1/documents/${String(documentId)}/edit-assignments/${String(id)}/validate`, {
    method: 'POST',
    body: JSON.stringify(body),
  });
}

export function returnEditAssignment(documentId: number, id: number, body: ReturnAssignmentRequest): Promise<EditAssignment> {
  return apiFetch<EditAssignment>(`/api/v1/documents/${String(documentId)}/edit-assignments/${String(id)}/return`, {
    method: 'POST',
    body: JSON.stringify(body),
  });
}

/** «Повторити для наступного місяця»: склад копіюється зі станами `Assigned`. */
export function copyEditAssignment(documentId: number, body: CopyAssignmentRequest): Promise<EditAssignmentBatch> {
  return apiFetch<EditAssignmentBatch>(`/api/v1/documents/${String(documentId)}/edit-assignments/copy`, {
    method: 'POST',
    body: JSON.stringify(body),
  });
}

/** Передача за збереженим набором на кілька зрізів одним викликом (атомарно на сервері). */
export function applyEditorSet(documentId: number, body: ApplyEditorSetRequest): Promise<EditAssignmentBatch> {
  return apiFetch<EditAssignmentBatch>(`/api/v1/documents/${String(documentId)}/edit-assignments/apply-set`, {
    method: 'POST',
    body: JSON.stringify(body),
  });
}

export function getCandidates(documentId: number, q: string): Promise<readonly Candidate[]> {
  return apiFetch<readonly Candidate[]>(
    `/api/v1/documents/${String(documentId)}/edit-assignments/candidates?q=${encodeURIComponent(q)}`,
  );
}

export function getEditorSets(): Promise<readonly EditorSet[]> {
  return apiFetch<readonly EditorSet[]>('/api/v1/me/editor-sets');
}

export function createEditorSet(body: SaveEditorSetRequest): Promise<EditorSet> {
  return apiFetch<EditorSet>('/api/v1/me/editor-sets', { method: 'POST', body: JSON.stringify(body) });
}

/** Зміна складу/назви набору; `baseVersion` - з `EditorSet.version`. */
export function updateEditorSet(id: number, body: SaveEditorSetRequest): Promise<EditorSet> {
  return apiFetch<EditorSet>(`/api/v1/me/editor-sets/${String(id)}`, { method: 'PUT', body: JSON.stringify(body) });
}

export function deleteEditorSet(id: number): Promise<void> {
  return apiFetch<void>(`/api/v1/me/editor-sets/${String(id)}`, { method: 'DELETE' });
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
    const dto = await apiFetch<MyEditAssignmentsView | null>('/api/v1/me/edit-assignments');

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
export function fillEditAssignment(documentId: number, id: number): Promise<EditAssignment> {
  return apiFetch<EditAssignment>(`/api/v1/documents/${String(documentId)}/edit-assignments/${String(id)}/fill`, {
    method: 'POST',
  });
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
