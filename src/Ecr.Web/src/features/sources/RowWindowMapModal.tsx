import { useState, type JSX } from 'react';
import { useReturnFocusOnUnmount } from '@/shared/a11y/focus';
import { Button, Divider, Group, List, Loader, Modal, Select, Stack, Switch, Table, Text, TextInput, Title } from '@mantine/core';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import type { DocumentSummary, SourceEntityStatus } from '@/api/types';
import { t } from '@/shared/i18n';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { showDone } from '@/shared/ui/notify';
import {
  emptyForm,
  formFromMap,
  newSourceRow,
  SummaryKinds,
  toCreateRequest,
  toUpdateRequest,
  validateForm,
  type RowWindowFormState,
  type RowWindowProblem,
  type SourceRow,
} from './rowWindowForm';
import { createRowWindowMap, updateRowWindowMap, type RowWindowMap, type RowWindowSummary } from './rowWindowApi';
import type { MapColumn } from './sourceEventMapForm';
import { fetchDynamicTables, fetchTableColumns, fetchUnits, SourceEventsKeys } from './sourceEventsData';

/** Назва способу згортки — літералами, щоб сторож каталогу бачив кожен ключ. */
export function summaryLabel(summary: RowWindowSummary): string {
  switch (summary) {
    case 'Total':
      return t('rowWindow.summaryTotal');
    case 'Average':
      return t('rowWindow.summaryAverage');
    case 'Minimum':
      return t('rowWindow.summaryMinimum');
    case 'Maximum':
      return t('rowWindow.summaryMaximum');
    case 'Count':
      return t('rowWindow.summaryCount');
  }
}

/** Текст проблеми форми — літералами, щоб сторож каталогу бачив кожен ключ. */
export function problemLabel(problem: RowWindowProblem): string {
  switch (problem) {
    case 'documentRequired':
      return t('rowWindow.problem.documentRequired');
    case 'tableRequired':
      return t('rowWindow.problem.tableRequired');
    case 'columnsRequired':
      return t('rowWindow.problem.columnsRequired');
    case 'windowNotDate':
      return t('rowWindow.problem.windowNotDate');
    case 'targetNotDecimal':
      return t('rowWindow.problem.targetNotDecimal');
    case 'windowSame':
      return t('rowWindow.problem.windowSame');
    case 'unitRequired':
      return t('rowWindow.problem.unitRequired');
    case 'policyInvalid':
      return t('rowWindow.problem.policyInvalid');
    case 'sourceIncomplete':
      return t('rowWindow.problem.sourceIncomplete');
    case 'selectorWithoutColumn':
      return t('rowWindow.problem.selectorWithoutColumn');
    case 'duplicateSelector':
      return t('rowWindow.problem.duplicateSelector');
  }
}

function columnOptions(columns: readonly MapColumn[], dataType: string | null): { value: string; label: string }[] {
  return columns
    .filter((column) => dataType === null || column.dataType === dataType)
    .map((column) => ({ value: String(column.id), label: `${column.code} · ${column.header} (${column.dataType})` }));
}

const toId = (value: string | null): number | null => (value === null ? null : Number(value));
const fromId = (value: number | null): string | null => (value === null ? null : String(value));

/**
 * Форма прив'язки вікна рядка (HSE301 A1, FEATURE-HSE301-VIEW §4.4): динамічна таблиця документа, колонки цілі
 * (`Decimal`), початку й кінця вікна (`Date`) та необов'язковий селектор, згортка, одиниця, пороги підтягування й
 * перелік джерел «значення селектора → атрибут».
 *
 * ⚠ Документ потрібен лише щоб прочитати колонки таблиці (прив'язка належить шаблону, а не документу), тому його
 * обирають і при зміні. Таблицю й колонку-ціль у наявній прив'язці змінити не можна (`PUT` їх не приймає) —
 * поля вимкнені.
 */
export function RowWindowMapModal({
  map,
  entities,
  defaultEntityId,
  documents,
  onClose,
}: {
  /** `null` — нова прив'язка. */
  readonly map: RowWindowMap | null;
  /** Сутності цього з'єднання: з них обираються джерела. */
  readonly entities: readonly SourceEntityStatus[];
  readonly defaultEntityId: number;
  readonly documents: readonly DocumentSummary[];
  readonly onClose: () => void;
}): JSX.Element {
  useReturnFocusOnUnmount();
  const queryClient = useQueryClient();
  const [state, setState] = useState<RowWindowFormState>(() => (map === null ? emptyForm() : formFromMap(map)));
  const patch = (next: Partial<RowWindowFormState>): void => setState((current) => ({ ...current, ...next }));
  const patchSource = (key: string, next: Partial<Omit<SourceRow, 'key'>>): void =>
    setState((current) => ({
      ...current,
      sources: current.sources.map((source) => (source.key === key ? { ...source, ...next } : source)),
    }));

  const tables = useQuery({
    queryKey: SourceEventsKeys.tables(state.documentId ?? 0),
    queryFn: () => fetchDynamicTables(state.documentId ?? 0),
    enabled: state.documentId !== null,
  });

  const table = tables.data?.find((item) => item.tableDefId === state.tableDefId);

  const columns = useQuery({
    queryKey: SourceEventsKeys.columns(state.documentId ?? 0, table?.tableInstanceId ?? 0),
    queryFn: () => fetchTableColumns(state.documentId ?? 0, table?.tableInstanceId ?? 0),
    enabled: state.documentId !== null && table !== undefined,
  });

  const units = useQuery({ queryKey: SourceEventsKeys.units, queryFn: fetchUnits });

  const columnList: readonly MapColumn[] = columns.data ?? [];
  const unitOptions = (units.data ?? []).map((unit) => ({ value: String(unit.id), label: unit.code }));
  const problems = validateForm(state, columnList);

  const save = useMutation({
    // ⚠ Відмову показує `ErrorAlert` у рендері — без `handled` сітка додала б тост (L9-01).
    meta: { handled: true },
    mutationFn: () => (map === null ? createRowWindowMap(toCreateRequest(state)) : updateRowWindowMap(map.id, toUpdateRequest(state, map))),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: SourceEventsKeys.rowWindowMapsAll });
      showDone(map === null ? t('rowWindow.created') : t('rowWindow.saved'));
      onClose();
    },
  });

  return (
    <Modal opened onClose={onClose} size="80rem" title={map === null ? t('rowWindow.createTitle') : t('rowWindow.editTitle')}>
      <Stack gap="sm" data-row-window-form="">
        <Group grow align="start">
          <Select
            label={t('rowWindow.document')}
            description={t('rowWindow.documentHint')}
            searchable
            required
            data={documents.map((document) => ({ value: String(document.id), label: document.businessKey }))}
            value={fromId(state.documentId)}
            onChange={(value) =>
              patch(
                map === null
                  ? { documentId: toId(value), tableDefId: null, targetColumnDefId: null, startColumnDefId: null, endColumnDefId: null, selectorColumnDefId: null }
                  : { documentId: toId(value) },
              )
            }
            data-row-window-document=""
          />
          <Select
            label={t('rowWindow.table')}
            required
            disabled={map !== null || state.documentId === null}
            data={(tables.data ?? []).map((item) => ({ value: String(item.tableDefId), label: `${item.tableCode} · ${item.sheetCode}` }))}
            value={fromId(state.tableDefId)}
            onChange={(value) =>
              patch({ tableDefId: toId(value), targetColumnDefId: null, startColumnDefId: null, endColumnDefId: null, selectorColumnDefId: null })
            }
            data-row-window-table=""
          />
        </Group>
        {tables.isError && <ErrorAlert error={tables.error} onRetry={() => void tables.refetch()} />}
        {tables.isSuccess && tables.data.length === 0 && (
          <Text size="sm" c="dimmed">
            {t('rowWindow.noDynamicTables')}
          </Text>
        )}
        {columns.isError && <ErrorAlert error={columns.error} onRetry={() => void columns.refetch()} />}
        {columns.isFetching && <Loader size="xs" />}

        <Group grow align="start">
          <Select
            label={t('rowWindow.targetColumn')}
            searchable
            required
            disabled={map !== null}
            data={columnOptions(columnList, 'Decimal')}
            value={fromId(state.targetColumnDefId)}
            onChange={(value) => patch({ targetColumnDefId: toId(value) })}
            data-row-window-target=""
          />
          <Select
            label={t('rowWindow.startColumn')}
            searchable
            required
            data={columnOptions(columnList, 'Date')}
            value={fromId(state.startColumnDefId)}
            onChange={(value) => patch({ startColumnDefId: toId(value) })}
            data-row-window-start=""
          />
          <Select
            label={t('rowWindow.endColumn')}
            searchable
            required
            data={columnOptions(columnList, 'Date')}
            value={fromId(state.endColumnDefId)}
            onChange={(value) => patch({ endColumnDefId: toId(value) })}
            data-row-window-end=""
          />
          <Select
            label={t('rowWindow.selectorColumn')}
            description={t('rowWindow.selectorColumnHint')}
            searchable
            clearable
            data={columnOptions(columnList, null)}
            value={fromId(state.selectorColumnDefId)}
            onChange={(value) => patch({ selectorColumnDefId: toId(value) })}
            data-row-window-selector=""
          />
        </Group>

        <Group grow align="end">
          <Select
            label={t('rowWindow.summary')}
            allowDeselect={false}
            data={SummaryKinds.map((kind) => ({ value: kind, label: summaryLabel(kind) }))}
            value={state.summary}
            onChange={(value) => patch({ summary: SummaryKinds.find((kind) => kind === value) ?? 'Total' })}
            data-row-window-summary=""
          />
          <Select
            label={t('rowWindow.targetUnit')}
            searchable
            required
            data={unitOptions}
            value={fromId(state.targetUnitId)}
            onChange={(value) => patch({ targetUnitId: toId(value) })}
            data-row-window-unit=""
          />
          <Switch
            label={t('rowWindow.isStep')}
            checked={state.isStep}
            onChange={(event) => patch({ isStep: event.currentTarget.checked })}
            data-row-window-step=""
          />
        </Group>

        <Group grow align="start">
          <TextInput
            label={t('rowWindow.minPercentGood')}
            inputMode="decimal"
            value={state.minPercentGood}
            onChange={(event) => patch({ minPercentGood: event.currentTarget.value })}
            data-row-window-min-good=""
          />
          <TextInput
            label={t('rowWindow.refetchWithinDays')}
            inputMode="numeric"
            value={state.refetchWithinDays}
            onChange={(event) => patch({ refetchWithinDays: event.currentTarget.value })}
            data-row-window-refetch=""
          />
          <TextInput
            label={t('rowWindow.maxGapSeconds')}
            inputMode="numeric"
            value={state.maxGapSeconds}
            onChange={(event) => patch({ maxGapSeconds: event.currentTarget.value })}
            data-row-window-gap=""
          />
        </Group>

        {map !== null && (
          <Switch
            label={t('rowWindow.activeSwitch')}
            checked={state.isActive}
            onChange={(event) => patch({ isActive: event.currentTarget.checked })}
            data-row-window-active=""
          />
        )}

        <Divider />
        <Title order={5}>{t('rowWindow.sourcesTitle')}</Title>
        <Table.ScrollContainer minWidth={820}>
          <Table data-row-window-sources="">
            <Table.Tbody>
              {state.sources.map((source) => (
                <Table.Tr key={source.key} data-row-window-source={source.key}>
                  <Table.Td>
                    <TextInput
                      size="xs"
                      aria-label={t('rowWindow.sourceSelectorValue')}
                      placeholder={t('rowWindow.sourceSelectorValue')}
                      value={source.selectorValue}
                      onChange={(event) => patchSource(source.key, { selectorValue: event.currentTarget.value })}
                    />
                  </Table.Td>
                  <Table.Td>
                    <Select
                      size="xs"
                      aria-label={t('rowWindow.sourceEntity')}
                      placeholder={t('rowWindow.sourceEntity')}
                      data={entities.map((entity) => ({ value: String(entity.id), label: entity.displayName ?? entity.code }))}
                      value={fromId(source.sourceEntityId)}
                      onChange={(value) => patchSource(source.key, { sourceEntityId: toId(value) })}
                    />
                  </Table.Td>
                  <Table.Td>
                    <TextInput
                      size="xs"
                      aria-label={t('rowWindow.sourceField')}
                      placeholder={t('rowWindow.sourceField')}
                      value={source.sourceField}
                      onChange={(event) => patchSource(source.key, { sourceField: event.currentTarget.value })}
                    />
                  </Table.Td>
                  <Table.Td>
                    <Select
                      size="xs"
                      searchable
                      aria-label={t('rowWindow.sourceUnit')}
                      placeholder={t('rowWindow.sourceUnit')}
                      data={unitOptions}
                      value={fromId(source.sourceUnitId)}
                      onChange={(value) => patchSource(source.key, { sourceUnitId: toId(value) })}
                    />
                  </Table.Td>
                  <Table.Td>
                    <Button
                      size="compact-xs"
                      variant="subtle"
                      color="statusError"
                      onClick={() => patch({ sources: state.sources.filter((item) => item.key !== source.key) })}
                    >
                      {t('rowWindow.sourceRemove')}
                    </Button>
                  </Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        </Table.ScrollContainer>
        <Group>
          <Button
            size="xs"
            variant="default"
            onClick={() => patch({ sources: [...state.sources, newSourceRow({ sourceEntityId: defaultEntityId })] })}
            data-row-window-source-add=""
          >
            {t('rowWindow.sourceAdd')}
          </Button>
        </Group>

        <Divider />
        {problems.length > 0 && (
          <Stack gap="xs" data-row-window-problems="">
            <Text size="sm" fw={600}>
              {t('rowWindow.cannotSave')}
            </Text>
            <List size="sm">
              {problems.map((problem) => (
                <List.Item key={problem} data-row-window-problem={problem}>
                  {problemLabel(problem)}
                </List.Item>
              ))}
            </List>
          </Stack>
        )}
        {save.error !== null && <ErrorAlert error={save.error} />}

        <Group justify="flex-end">
          <Button variant="default" onClick={onClose}>
            {t('common.cancel')}
          </Button>
          <Button disabled={problems.length > 0} loading={save.isPending} onClick={() => save.mutate()} data-row-window-save="">
            {t('common.save')}
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}
