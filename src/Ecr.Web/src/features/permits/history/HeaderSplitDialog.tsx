import { useState, type JSX } from 'react';
import { Alert, Button, Group, Modal, Stack, Table, Text, TextInput } from '@mantine/core';
import { useQueries } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import { queryKeys } from '@/api/queryKeys';
import type { TableSliceDto } from '@/api/types';
import { usePendingLoading } from '@/features/common/usePendingLoading';
import { decimalTextOf } from '@/features/grid/rounding';
import { formatDecimal } from '@/shared/format/number';
import { formatMonthYear } from '@/shared/format/period';
import { localized } from '@/shared/i18n/localized';
import { t } from '@/shared/i18n';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { rowKeyOfLine, type HeaderSplitLine } from './headerSplitModel';
import { rowDisplayName } from './rowName';
import { dayMonth, dottedDate, monthOf, shortDecimal, totalOfParts, wireDecimal } from './splitModel';
import { useSaveCellSplits } from './usePermitHistory';

const PartLetters = 'ABCDEFGH';

function shown(value: string): string {
  return formatDecimal(value) ?? value;
}

/** The slices of the tables the lines belong to (row names, row versions, units) — the grid's own cache key. */
export function useLineSlices(documentId: number, periodKey: number, lines: readonly HeaderSplitLine[]): Map<number, TableSliceDto> {
  const ids = [...new Set(lines.flatMap((line) => (line.table === null ? [] : [line.table.tableInstanceId])))];
  const slices = useQueries({
    queries: ids.map((id) => ({
      queryKey: queryKeys.slices.one(id, periodKey),
      queryFn: () => apiFetch<TableSliceDto>(`/api/v1/documents/${String(documentId)}/tables/${String(id)}`),
    })),
  });

  const map = new Map<number, TableSliceDto>();
  ids.forEach((id, index) => {
    const data = slices[index]?.data;
    if (data !== undefined) map.set(id, data);
  });

  return map;
}

export function lineKey(line: HeaderSplitLine): string {
  return `${String(line.row.tableDefId)}#${String(line.row.sheetCopyId ?? '')}:${line.row.rowKey}:${line.columnCode}`;
}

export interface HeaderSplitDialogProps {
  readonly documentId: number;
  readonly periodKey: number;
  readonly lines: readonly HeaderSplitLine[];
  readonly slices: ReadonlyMap<number, TableSliceDto>;
  readonly onClose: () => void;
}

/**
 * Feature (j) Ship 2, jh3 (mockup light-1335-jh3): «Split February 2026 — 7 rows». Every row relying on the header permit,
 * one input per permit and the total; a row not split yet shows «was X» (its single value). ⛔ ECR never divides by days:
 * nothing is pre-computed here. Partial save: only the rows typed are sent; Submit stays blocked until all are split.
 */
export function HeaderSplitDialog({ documentId, periodKey, lines, slices, onClose }: HeaderSplitDialogProps): JSX.Element {
  const save = useSaveCellSplits(documentId);
  const saveLoading = usePendingLoading(save.isPending);
  const [texts, setTexts] = useState<ReadonlyMap<string, readonly string[]>>(new Map());

  const first = lines[0];
  const parts = first?.month.parts ?? [];
  const { year, month } = first === undefined ? { year: 0, month: 0 } : monthOf(first.month.month);
  const monthLabel = formatMonthYear(year, month);
  const changedOn = parts[1]?.dayFrom;
  // F13 p223 (3): rows, as the banner counts them — a row is split when every one of its month cells holds all parts.
  const rowKeys = [...new Set(lines.map(rowKeyOfLine))];
  const rowCount = rowKeys.length;

  const valuesOf = (line: HeaderSplitLine): readonly string[] =>
    texts.get(lineKey(line)) ?? parts.map((_, index) => shortDecimal(wireDecimal(line.cell?.parts[index])) ?? '');
  const parsedOf = (line: HeaderSplitLine): readonly (string | null)[] =>
    valuesOf(line).map((text) => (text.trim().length === 0 ? null : decimalTextOf(text)));
  const invalidOf = (line: HeaderSplitLine): boolean =>
    valuesOf(line).some((text, index) => text.trim().length > 0 && parsedOf(line)[index] === null);

  const done = rowKeys.filter((key) =>
    lines.filter((line) => rowKeyOfLine(line) === key).every((line) => totalOfParts(parsedOf(line)) !== null),
  ).length;
  const anyInvalid = lines.some(invalidOf);

  const submit = (): void => {
    const items = lines
      .filter((line) => line.table !== null && texts.has(lineKey(line)))
      .map((line) => {
        const slice = slices.get(line.table?.tableInstanceId ?? 0);
        return {
          tableInstanceId: line.table?.tableInstanceId ?? 0,
          rowKey: line.row.rowKey,
          columnCode: line.columnCode,
          baseVersion: slice?.rows.find((candidate) => candidate.rowKey === line.row.rowKey)?.rowVersion ?? '',
          parts: [...parsedOf(line)],
        };
      });
    if (items.length === 0) {
      onClose();
      return;
    }
    save.mutate({ periodKey, items }, { onSuccess: onClose });
  };

  return (
    <Modal opened onClose={onClose} size="xl" title={t('permits.header.dialogTitle', { month: monthLabel, count: rowCount })} data-header-split-dialog="">
      <form
        onSubmit={(event) => {
          event.preventDefault();
          if (!anyInvalid) submit();
        }}
      >
        <Stack gap="sm">
          {changedOn !== undefined && (
            <Alert color="blue" variant="light" data-header-split-why="">
              {t('permits.header.dialogWhy', { date: dottedDate(changedOn), month: monthLabel })}
            </Alert>
          )}
          <Table.ScrollContainer minWidth={640}>
            <Table verticalSpacing={4}>
              <Table.Thead>
                <Table.Tr>
                  <Table.Th>{t('permits.header.table')}</Table.Th>
                  <Table.Th>{t('permits.header.row')}</Table.Th>
                  {parts.map((part) => (
                    <Table.Th key={part.partNo}>
                      <Text size="sm" fw={600}>
                        {t('permits.split.part', { part: PartLetters[part.partNo - 1] ?? String(part.partNo) })}
                      </Text>
                      <Text size="xs" c="dimmed">{`${part.permit} · ${dayMonth(part.dayFrom)}–${dayMonth(part.dayTo)}`}</Text>
                    </Table.Th>
                  ))}
                  <Table.Th ta="right">{t('permits.header.total')}</Table.Th>
                  <Table.Th>{t('permits.header.unit')}</Table.Th>
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {lines.map((line) => {
                  const slice = slices.get(line.table?.tableInstanceId ?? 0);
                  const column = slice?.columns.find((candidate) => candidate.code === line.columnCode);
                  const rowName = rowDisplayName(slice, line.row.rowKey, line.row.permits[0]?.permit);
                  const values = valuesOf(line);
                  const parsed = parsedOf(line);
                  const total = totalOfParts(parsed);
                  const was = shortDecimal(wireDecimal(line.cell?.total));
                  const multi = lines.filter((other) => other.row === line.row).length > 1;

                  return (
                    <Table.Tr key={lineKey(line)} data-header-split-line={line.row.rowKey} data-split-pending={total === null ? '' : undefined}>
                      <Table.Td>{line.table === null ? line.row.tableCode : localized(line.table.tableNameL10n) || line.row.tableCode}</Table.Td>
                      <Table.Td>{multi ? `${rowName} · ${column?.code ?? line.columnCode}` : rowName}</Table.Td>
                      {parts.map((part, index) => (
                        <Table.Td key={part.partNo}>
                          <TextInput
                            size="xs"
                            inputMode="decimal"
                            aria-label={`${rowName} · ${t('permits.split.part', { part: PartLetters[index] ?? String(part.partNo) })}`}
                            value={values[index] ?? ''}
                            disabled={line.table === null}
                            onChange={(event) => {
                              const next = [...values];
                              next[index] = event.currentTarget.value;
                              setTexts((current) => new Map(current).set(lineKey(line), next));
                              if (save.error !== null) save.reset();
                            }}
                            error={values[index]?.trim().length !== 0 && parsed[index] === null ? t('permits.split.notNumber') : undefined}
                            styles={{ input: { textAlign: 'right' } }}
                          />
                        </Table.Td>
                      ))}
                      <Table.Td ta="right">
                        {total !== null ? (
                          <Text size="sm" fw={600} c="statusSuccess">{shown(total)}</Text>
                        ) : (
                          <Text size="sm" c="statusWarning" data-split-was="">
                            {was === null ? '—' : t('permits.header.was', { value: shown(was) })}
                          </Text>
                        )}
                      </Table.Td>
                      <Table.Td>{column?.unitSymbol ?? ''}</Table.Td>
                    </Table.Tr>
                  );
                })}
              </Table.Tbody>
            </Table>
          </Table.ScrollContainer>
          <ErrorAlert error={save.error} />
          <Group justify="space-between" wrap="nowrap" align="flex-start">
            <Text size="xs" c="dimmed" role="status" aria-live="polite" data-header-split-progress="">
              <Text span fw={600} c="var(--mantine-color-text)" size="xs">
                {t('permits.header.progress', { done, count: rowCount })}
              </Text>{' '}
              {t('permits.header.progressHint')}
            </Text>
            <Group gap="xs" wrap="nowrap">
              <Button variant="default" onClick={onClose}>
                {t('common.cancel')}
              </Button>
              <Button type="submit" loading={saveLoading} disabled={anyInvalid}>
                {t('permits.split.save')}
              </Button>
            </Group>
          </Group>
        </Stack>
      </form>
    </Modal>
  );
}
