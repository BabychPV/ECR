import type { JSX } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { queryKeys } from '@/api/queryKeys';
import type { TableSliceDto } from '@/api/types';
import { CellSplitDialog } from './CellSplitDialog';
import { rowDisplayName } from './rowName';
import { cellKey, permitGridMarksOf } from './splitModel';
import { closeCellSplit, type SplitTarget } from './splitRequest';
import { usePermitHistory } from './usePermitHistory';

/**
 * Feature (j): the split dialog for the requested cell, with what the grid already holds — the row's name, the month
 * column's unit and the row version (`baseVersion` of the save) come from the cached table slice; the parts and the
 * permits from the permit history.
 */
export function CellSplitDialogHost({ target }: { readonly target: SplitTarget }): JSX.Element {
  const queryClient = useQueryClient();
  const history = usePermitHistory(target.documentId, target.periodKey);
  const slice = queryClient.getQueryData<TableSliceDto>(queryKeys.slices.one(target.tableInstanceId, target.periodKey));
  const row = slice?.rows.find((candidate) => candidate.rowKey === target.rowKey);
  const column = slice?.columns.find((candidate) => candidate.code === target.columnCode);
  const mark =
    permitGridMarksOf(history.data, target.tableDefId, target.sheetCopyId).cells.get(cellKey(target.rowKey, target.columnCode)) ??
    null;

  return (
    <CellSplitDialog
      key={cellKey(target.rowKey, target.columnCode)}
      target={target}
      mark={mark}
      historyPending={history.isPending}
      rowLabel={rowDisplayName(
        slice,
        target.rowKey,
        permitGridMarksOf(history.data, target.tableDefId, target.sheetCopyId).rows.get(target.rowKey)?.permits[0]?.permit,
      )}
      unit={column?.unitSymbol ?? null}
      baseVersion={row?.rowVersion ?? null}
      fromHeader={permitGridMarksOf(history.data, target.tableDefId, target.sheetCopyId).rows.get(target.rowKey)?.permitSource === 'header'}
      onClose={closeCellSplit}
    />
  );
}
