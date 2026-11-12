import { useMutation, useQuery, useQueryClient, type QueryClient, type UseQueryResult } from '@tanstack/react-query';
import { queryKeys } from '@/api/queryKeys';
import { invalidateSlices } from '@/features/grid/sliceCache';
import {
  addHeaderPermit,
  addRowPermit,
  getPermitHistory,
  permitHistoryKey,
  saveCellSplits,
  undoHeaderPermit,
  undoRowPermit,
  type AddHeaderPermitRequest,
  type AddRowPermitRequest,
  type UndoHeaderPermitRequest,
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
 * table slices of the document for the period are refetched.
 */
export async function refreshAfterPermitChange(
  queryClient: QueryClient,
  documentId: number,
  periodKey: number,
): Promise<void> {
  await Promise.all([
    queryClient.invalidateQueries({ queryKey: queryKeys.document.ofDocument(documentId) }),
    // ⛔ W-5 (audit 2026-11): targeted (`CL-02`/`C1-01`) — the visible slices of the document for the period are
    // refetched; hidden mounted grids («one table» mode) and the rest only become stale (read again when shown/mounted).
    // `slices.all()` refetched every mounted grid (on Land up to 5.8 MB each).
    invalidateSlices(queryClient, { documentId, periodKey }),
    // F13 p223 (1): the server re-ran the stored check — the page's Issues (`DocumentPage`) are read again.
    queryClient.invalidateQueries({ queryKey: ['validation', documentId] }),
  ]);
}

/** j1: add a permit to a row from a date. The refusal is shown in the form itself (`handled`). */
export function useAddRowPermit(documentId: number, periodKey: number) {
  const queryClient = useQueryClient();

  return useMutation({
    meta: { handled: true },
    mutationFn: (body: AddRowPermitRequest) => addRowPermit(documentId, body),
    onSuccess: () => {
      void refreshAfterPermitChange(queryClient, documentId, periodKey);
    },
  });
}

/** j1b: undo the latest permit added to a row. The refusal is shown next to the button (`handled`). */
export function useUndoRowPermit(documentId: number, periodKey: number) {
  const queryClient = useQueryClient();

  return useMutation({
    meta: { handled: true },
    mutationFn: (body: UndoRowPermitRequest) => undoRowPermit(documentId, body),
    onSuccess: () => {
      void refreshAfterPermitChange(queryClient, documentId, periodKey);
    },
  });
}

/** jh1: add a permit to a header field from a date. The refusal is shown in the form itself (`handled`). */
export function useAddHeaderPermit(documentId: number) {
  const queryClient = useQueryClient();

  return useMutation({
    meta: { handled: true },
    mutationFn: (body: AddHeaderPermitRequest) => addHeaderPermit(documentId, body),
    onSuccess: (_result, body) => {
      void refreshAfterPermitChange(queryClient, documentId, body.periodKey);
    },
  });
}

/** jh1: undo the latest permit added to a header field. The refusal is shown next to the link (`handled`). */
export function useUndoHeaderPermit(documentId: number, periodKey: number) {
  const queryClient = useQueryClient();

  return useMutation({
    meta: { handled: true },
    mutationFn: (body: UndoHeaderPermitRequest) => undoHeaderPermit(documentId, body),
    onSuccess: () => {
      void refreshAfterPermitChange(queryClient, documentId, periodKey);
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

      void refreshAfterPermitChange(queryClient, documentId, body.periodKey);
    },
  });
}
