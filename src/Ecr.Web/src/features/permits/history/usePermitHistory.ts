import { useMutation, useQuery, useQueryClient, type QueryClient, type UseQueryResult } from '@tanstack/react-query';
import { queryKeys } from '@/api/queryKeys';
import {
  addRowPermit,
  getPermitHistory,
  permitHistoryKey,
  saveCellSplits,
  undoRowPermit,
  type AddRowPermitRequest,
  type DocumentPermitHistory,
  type SaveCellSplitsRequest,
  type UndoRowPermitRequest,
} from './api';

/**
 * Feature (j) P3: the document's permit history for the period (one request for every grid of the page — same key).
 */
export function usePermitHistory(
  documentId: number,
  periodKey: number,
  enabled = true,
): UseQueryResult<DocumentPermitHistory> {
  return useQuery({
    queryKey: permitHistoryKey(documentId, periodKey),
    queryFn: () => getPermitHistory(documentId, periodKey),
    enabled,
  });
}

/**
 * After a permit is added/undone or a month is split: the history, the cell values (the split writes the total), the
 * table status and the validation of the document change — the document prefix (history, status, validation) and the
 * table slices are refetched.
 */
export async function refreshAfterPermitChange(queryClient: QueryClient, documentId: number): Promise<void> {
  await Promise.all([
    queryClient.invalidateQueries({ queryKey: queryKeys.document.ofDocument(documentId) }),
    queryClient.invalidateQueries({ queryKey: queryKeys.slices.all() }),
  ]);
}

/** j1: add a permit to a row from a date. The refusal is shown in the form itself (`handled`). */
export function useAddRowPermit(documentId: number) {
  const queryClient = useQueryClient();

  return useMutation({
    meta: { handled: true },
    mutationFn: (body: AddRowPermitRequest) => addRowPermit(documentId, body),
    onSuccess: () => {
      void refreshAfterPermitChange(queryClient, documentId);
    },
  });
}

/** j1b: undo the latest permit added to a row. The refusal is shown next to the button (`handled`). */
export function useUndoRowPermit(documentId: number) {
  const queryClient = useQueryClient();

  return useMutation({
    meta: { handled: true },
    mutationFn: (body: UndoRowPermitRequest) => undoRowPermit(documentId, body),
    onSuccess: () => {
      void refreshAfterPermitChange(queryClient, documentId);
    },
  });
}

/** j2: save the parts of month cells. The refusal is shown in the dialog (`handled`). */
export function useSaveCellSplits(documentId: number) {
  const queryClient = useQueryClient();

  return useMutation({
    meta: { handled: true },
    mutationFn: (body: SaveCellSplitsRequest) => saveCellSplits(documentId, body),
    onSuccess: (history, body) => {
      queryClient.setQueryData(permitHistoryKey(documentId, body.periodKey), history);

      void refreshAfterPermitChange(queryClient, documentId);
    },
  });
}
