import { useState, type JSX } from 'react';
import { Button, Group, Text } from '@mantine/core';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { queryKeys } from '@/api/queryKeys';
import type { TableSliceDto } from '@/api/types';
import { isPermitTarget, lookupTargetsQuery } from '@/features/registries/lookupTargets';
import { formatDateTime } from '@/shared/format/datetime';
import { formatDecimal } from '@/shared/format/number';
import { t } from '@/shared/i18n';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { AddRowPermitForm } from './AddRowPermitForm';
import { cellKey, dayMonth, dottedDate, permitGridMarksOf, wireDecimal } from './splitModel';
import { requestCellSplit } from './splitRequest';
import { usePermitHistory, useUndoRowPermit } from './usePermitHistory';

/** What the Inspector knows of the cell under the cursor (a subset of `InspectedCell`). */
export interface RowPermitCell {
  readonly tableInstanceId: number;
  readonly periodKey: number;
  readonly rowKey: string;
  readonly rowLabel: string;
  readonly columnCode: string;
  readonly isCalculated: boolean;
  readonly isReadOnly: boolean;
  readonly value: string;
}

export interface RowPermitSectionProps {
  readonly documentId: number;
  readonly periodKey: number;
  readonly tableDefId: number;
  readonly sheetCopyId: number | null;
  readonly cell: RowPermitCell;
}

function shown(value: unknown): string {
  const text = wireDecimal(value);

  return text === null ? '—' : (formatDecimal(text) ?? text);
}

/**
 * Feature (j) Inspector «Info» of a row with a permit:
 *  - the permit cell — the row's permit history (who added which permit, from when), «Undo last» and
 *    «Add permit from date…» (j1a/j1b);
 *  - a month cell shared by two permits — the total and the parts, «Edit parts» opens the split dialog (j2);
 *  - a calculated cell of a row with history — why it is not split (the coordinator's note).
 */
export function RowPermitSection({ documentId, periodKey, tableDefId, sheetCopyId, cell }: RowPermitSectionProps): JSX.Element | null {
  const queryClient = useQueryClient();
  const slice = queryClient.getQueryData<TableSliceDto>(queryKeys.slices.one(cell.tableInstanceId, periodKey));
  const column = slice?.columns.find((candidate) => candidate.code === cell.columnCode);
  const isLookup = column?.dataType === 'Lookup' && column.lookupRegistryDefId !== null;
  const targets = useQuery({ ...lookupTargetsQuery(), enabled: isLookup });
  const registry = isLookup ? targets.data?.find((candidate) => candidate.id === column.lookupRegistryDefId) : undefined;
  const isPermitColumn = registry !== undefined && isPermitTarget(registry);

  const history = usePermitHistory(documentId, periodKey);
  const marks = permitGridMarksOf(history.data, tableDefId, sheetCopyId);
  const row = marks.rows.get(cell.rowKey);
  const mark = marks.cells.get(cellKey(cell.rowKey, cell.columnCode));

  const undo = useUndoRowPermit(documentId);
  const [adding, setAdding] = useState(false);
  const [added, setAdded] = useState<string | null>(null);

  const splitTarget = {
    documentId,
    periodKey,
    tableInstanceId: cell.tableInstanceId,
    tableDefId,
    sheetCopyId,
    rowKey: cell.rowKey,
    columnCode: cell.columnCode,
  };

  if (mark !== undefined) {
    const parts = mark.month.parts;

    return (
      <section className="ecr-insp-permit" aria-label={t('permits.history.splitSection')} data-permit-split-info="">
        <h3 className="ecr-insp-group">
          <span>{t('permits.history.splitSection')}</span>
        </h3>
        <Text size="sm">{t('permits.history.total', { total: shown(mark.cell?.total) })}</Text>
        {parts.map((part, index) => (
          <Text size="sm" key={part.partNo} data-permit-part={part.partNo}>
            {`${String.fromCharCode(64 + part.partNo)}: ${shown(mark.cell?.parts[index])} · ${part.permit} · ${dayMonth(part.dayFrom)}–${dayMonth(part.dayTo)}`}
          </Text>
        ))}
        <Text size="xs" c="dimmed">
          {mark.needsSplit ? t('permits.history.splitNeeded') : t('permits.history.splitWhy')}
        </Text>
        {!cell.isReadOnly && (
          <Button size="xs" variant="default" mt="xs" data-permit-split-open="" onClick={() => requestCellSplit(splitTarget)}>
            {t('permits.history.editParts')}
          </Button>
        )}
      </section>
    );
  }

  if (row !== undefined && cell.isCalculated) {
    return (
      <Text size="xs" c="dimmed" p="sm" data-permit-calculated-note="">
        {t('permits.history.calculatedNotSplit')}
      </Text>
    );
  }

  if (!isPermitColumn || cell.value.length === 0) return null;

  const currentEntryId = Number(cell.value);
  const canUndo = row !== undefined && row.permits.some((item) => item.segmentId !== null);

  return (
    <section className="ecr-insp-permit" aria-label={t('permits.history.title')} data-permit-history="">
      {row !== undefined && (
        <>
          <h3 className="ecr-insp-group">
            <span>{t('permits.history.title')}</span>
          </h3>
          <ul className="ecr-insp-permit-list">
            {row.permits.map((item) => (
              <li key={`${String(item.registryEntryId)}:${item.from ?? ''}`} data-permit-history-item="">
                <Text size="sm" ff="monospace">
                  {item.permit}
                </Text>
                <Text size="xs" c="dimmed">
                  {item.from === null
                    ? t('permits.history.until', { until: item.until === null ? '—' : dottedDate(item.until) })
                    : t('permits.history.from', { from: dottedDate(item.from) })}
                  {item.addedBy !== null && item.addedAt !== null
                    ? ` · ${t('permits.history.addedBy', { user: item.addedBy, time: formatDateTime(new Date(item.addedAt)) })}`
                    : ''}
                </Text>
              </li>
            ))}
          </ul>
        </>
      )}

      {added !== null && (
        <Text size="xs" role="status" data-permit-added="">
          {t('permits.history.added', { date: dottedDate(added), row: cell.rowLabel })}
        </Text>
      )}
      <ErrorAlert error={undo.error} />

      {!cell.isReadOnly && registry !== undefined && (
        adding ? (
          <AddRowPermitForm
            documentId={documentId}
            periodKey={periodKey}
            tableInstanceId={cell.tableInstanceId}
            rowKey={cell.rowKey}
            rowLabel={cell.rowLabel}
            columnCode={cell.columnCode}
            registryCode={registry.code}
            currentEntryId={Number.isFinite(currentEntryId) ? currentEntryId : null}
            onDone={(date) => {
              setAdding(false);
              setAdded(date);
            }}
            onCancel={() => setAdding(false)}
          />
        ) : (
          <Group gap="xs" mt="xs">
            <Button size="xs" variant="default" data-permit-add-open="" onClick={() => setAdding(true)}>
              {t('permits.history.add')}
            </Button>
            {canUndo && (
              <Button
                size="xs"
                variant="subtle"
                loading={undo.isPending}
                data-permit-undo=""
                onClick={() => {
                  setAdded(null);
                  undo.mutate({ tableInstanceId: cell.tableInstanceId, rowKey: cell.rowKey, columnCode: cell.columnCode });
                }}
              >
                {t('permits.history.undo')}
              </Button>
            )}
          </Group>
        )
      )}
    </section>
  );
}
