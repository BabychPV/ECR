import { useMutation, useQueryClient, type UseMutationResult } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import { queryKeys } from '@/api/queryKeys';
import type { ReturnAssignmentRequest, ValidateAssignmentRequest } from './types';

const base = (documentId: number): string => `/api/v1/documents/${String(documentId)}/edit-assignments`;

interface ValidateVariables {
  readonly id: number;
  readonly baseVersion: number;
}

interface ReturnVariables extends ReturnAssignmentRequest {
  readonly id: number;
}

/**
 * Після будь-якого результату (і успіху, і `409`/`422`) перелік передач і
 * «Мої задачі» перечитуються: відмова `editAssignmentStale` означає, що стан
 * змінився, і власник має побачити актуальний, а не повторити те саме.
 * Дозволи комірок (`['document', id]`) теж залежать від замків виконавців.
 */
function useRefreshAfter(documentId: number): () => Promise<void> {
  const client = useQueryClient();

  return async () => {
    await Promise.all([
      client.invalidateQueries({ queryKey: queryKeys.editAssignments.forDocument(documentId) }),
      client.invalidateQueries({ queryKey: queryKeys.editAssignments.mine() }),
      client.invalidateQueries({ queryKey: queryKeys.document.ofDocument(documentId) }),
    ]);
  };
}

/** `POST …/{id}/validate` - ідемпотентно для вже `Validated`. */
export function useValidateAssignment(documentId: number): UseMutationResult<void, Error, ValidateVariables> {
  const refresh = useRefreshAfter(documentId);

  return useMutation({
    mutationFn: ({ id, baseVersion }: ValidateVariables) =>
      apiFetch<void>(`${base(documentId)}/${String(id)}/validate`, {
        method: 'POST',
        body: JSON.stringify({ baseVersion } satisfies ValidateAssignmentRequest),
      }),
    onSettled: refresh,
  });
}

/** `POST …/{id}/return` - коментар обов'язковий, 1-500 символів. */
export function useReturnAssignment(documentId: number): UseMutationResult<void, Error, ReturnVariables> {
  const refresh = useRefreshAfter(documentId);

  return useMutation({
    mutationFn: ({ id, ...body }: ReturnVariables) =>
      apiFetch<void>(`${base(documentId)}/${String(id)}/return`, {
        method: 'POST',
        body: JSON.stringify(body satisfies ReturnAssignmentRequest),
      }),
    onSettled: refresh,
  });
}
