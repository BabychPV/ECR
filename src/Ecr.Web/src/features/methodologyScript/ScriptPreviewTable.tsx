import { ActionIcon, Badge, Group, Stack, Table, Text, Title } from '@mantine/core';
import { Fragment, useMemo, useState, type JSX } from 'react';
import { t } from '@/shared/i18n';
import type {
  ScriptPreviewDto,
  ScriptPreviewIssue,
  ScriptPreviewRowDto,
  ScriptPreviewRowState,
  ScriptPreviewTableDto,
  ScriptPreviewValueDto,
} from './types';

export interface ScriptPreviewTableProps {
  readonly preview: ScriptPreviewDto;
  /** Імена виходів (колонки таблиці); решта значень — у розгортанні рядка. */
  readonly outputNames: readonly string[];
  /** «Створити тест із рядка» — кнопка з'являється, лише коли передано. */
  readonly onCreateTest?: (row: ScriptPreviewRowDto, table: ScriptPreviewTableDto) => void;
}

function stateLabel(state: ScriptPreviewRowState): string {
  switch (state) {
    case 'selected':
      return t('methodologyScript.preview.state.selected');
    case 'inactive':
      return t('methodologyScript.preview.state.inactive');
    case 'notSelected':
      return t('methodologyScript.preview.state.notSelected');
    case 'rejected':
      return t('methodologyScript.preview.state.rejected');
  }
}

function stateColor(state: ScriptPreviewRowState): string {
  switch (state) {
    case 'selected':
      return 'green';
    case 'rejected':
      return 'statusError';
    case 'inactive':
      return 'statusWarning';
    case 'notSelected':
      return 'gray';
  }
}

function issueLabel(issue: ScriptPreviewIssue): string {
  switch (issue) {
    case 'missingInputs':
      return t('methodologyScript.preview.issue.missingInputs');
    case 'stepError':
      return t('methodologyScript.preview.issue.stepError');
    case 'rejected':
      return t('methodologyScript.preview.issue.rejected');
  }
}

interface OutputColumn {
  readonly name: string;
  readonly substance: string | null;
}

function columnKey(column: OutputColumn): string {
  return `${column.name}@${column.substance ?? ''}`;
}

function columnsOf(table: ScriptPreviewTableDto, outputNames: readonly string[]): OutputColumn[] {
  const columns: OutputColumn[] = [];
  for (const name of outputNames) {
    const substances: string[] = [];
    for (const row of table.rows) {
      for (const value of row.values) {
        if (value.name === name && value.substance !== undefined && value.substance !== null) {
          if (!substances.includes(value.substance)) substances.push(value.substance);
        }
      }
    }
    if (substances.length === 0) columns.push({ name, substance: null });
    else for (const substance of substances) columns.push({ name, substance });
  }
  return columns;
}

function ValueCell({ value }: { readonly value: ScriptPreviewValueDto | undefined }): JSX.Element {
  if (value === undefined) return <Text c="dimmed">—</Text>;
  if (value.error !== undefined && value.error !== null && value.error !== '') {
    return (
      <Text c="statusError" size="sm" title={value.error}>
        {value.error}
      </Text>
    );
  }
  return <Text size="sm">{value.text}</Text>;
}

function findValue(
  row: ScriptPreviewRowDto,
  column: OutputColumn,
): ScriptPreviewValueDto | undefined {
  return row.values.find(
    (v) => v.name === column.name && (v.substance ?? null) === column.substance,
  );
}

function PreviewRow(props: {
  readonly row: ScriptPreviewRowDto;
  readonly table: ScriptPreviewTableDto;
  readonly columns: readonly OutputColumn[];
  readonly onCreateTest: ScriptPreviewTableProps['onCreateTest'];
}): JSX.Element {
  const { row, table, columns, onCreateTest } = props;
  const [open, setOpen] = useState(false);
  const expandable = row.values.length > 0;
  const span = columns.length + 4;

  return (
    <Fragment>
      <Table.Tr>
        <Table.Td w={36}>
          {expandable && (
            <ActionIcon
              variant="subtle"
              size="sm"
              aria-expanded={open}
              aria-label={
                open
                  ? t('methodologyScript.preview.collapseRow', { row: row.rowNumber })
                  : t('methodologyScript.preview.expandRow', { row: row.rowNumber })
              }
              onClick={() => {
                setOpen((v) => !v);
              }}
            >
              {open ? '▾' : '▸'}
            </ActionIcon>
          )}
        </Table.Td>
        <Table.Td>{row.rowNumber}</Table.Td>
        <Table.Td>
          <Badge color={stateColor(row.state)} variant="light">
            {stateLabel(row.state)}
          </Badge>
        </Table.Td>
        <Table.Td>
          {row.issue !== undefined && row.issue !== null && (
            <Stack gap="xs">
              <Text size="sm" c="statusError">
                {issueLabel(row.issue)}
              </Text>
              {row.missing !== undefined && row.missing !== null && row.missing.length > 0 && (
                <Text size="xs" c="dimmed" ff="monospace">
                  {row.missing.join(', ')}
                </Text>
              )}
            </Stack>
          )}
        </Table.Td>
        {columns.map((column) => (
          <Table.Td key={columnKey(column)}>
            <ValueCell value={findValue(row, column)} />
          </Table.Td>
        ))}
      </Table.Tr>
      {open && (
        <Table.Tr>
          <Table.Td colSpan={span}>
            <Group justify="space-between" align="flex-start">
              <Table withRowBorders={false} verticalSpacing={2} w="auto">
                <Table.Tbody>
                  {row.values.map((value, index) => (
                    <Table.Tr key={`${String(index)}-${value.name}-${value.substance ?? ''}`}>
                      <Table.Td ff="monospace">
                        {value.substance === undefined || value.substance === null
                          ? value.name
                          : `${value.name} [${value.substance}]`}
                      </Table.Td>
                      <Table.Td>
                        <ValueCell value={value} />
                      </Table.Td>
                      <Table.Td>
                        <Text size="xs" c="dimmed">
                          {value.kind}
                        </Text>
                      </Table.Td>
                    </Table.Tr>
                  ))}
                </Table.Tbody>
              </Table>
              {onCreateTest !== undefined && row.state === 'selected' && (
                <ActionIcon
                  variant="light"
                  aria-label={t('methodologyScript.preview.createTest')}
                  title={t('methodologyScript.preview.createTest')}
                  onClick={() => {
                    onCreateTest(row, table);
                  }}
                >
                  +
                </ActionIcon>
              )}
            </Group>
          </Table.Td>
        </Table.Tr>
      )}
    </Fragment>
  );
}

/** Результат пробного розрахунку: підсумки таблиць і рядки зі значеннями. */
export function ScriptPreviewTable({
  preview,
  outputNames,
  onCreateTest,
}: ScriptPreviewTableProps): JSX.Element {
  const columnsByTable = useMemo(
    () => preview.tables.map((table) => columnsOf(table, outputNames)),
    [preview.tables, outputNames],
  );

  if (preview.tables.length === 0) {
    return <Text c="dimmed">{t('methodologyScript.preview.empty')}</Text>;
  }

  return (
    <Stack gap="lg" data-testid="script-preview">
      {preview.tables.map((table, index) => {
        const columns = columnsByTable[index] ?? [];
        return (
          <Stack key={table.tableInstanceId} gap="xs">
            <Group gap="sm">
              <Title order={5}>{table.tableCode}</Title>
              <Text size="sm" c="dimmed">
                {t('methodologyScript.preview.summary', {
                  total: table.totalRows,
                  selected: table.selectedRows,
                  inactive: table.inactiveRows,
                })}
              </Text>
            </Group>
            <Table.ScrollContainer minWidth={480}>
              <Table striped highlightOnHover>
                <Table.Thead>
                  <Table.Tr>
                    <Table.Th w={36} />
                    <Table.Th>{t('methodologyScript.preview.col.row')}</Table.Th>
                    <Table.Th>{t('methodologyScript.preview.col.state')}</Table.Th>
                    <Table.Th>{t('methodologyScript.preview.col.issue')}</Table.Th>
                    {columns.map((column) => (
                      <Table.Th key={columnKey(column)}>
                        {column.substance === null
                          ? column.name
                          : `${column.name} [${column.substance}]`}
                      </Table.Th>
                    ))}
                  </Table.Tr>
                </Table.Thead>
                <Table.Tbody>
                  {table.rows.map((row) => (
                    <PreviewRow
                      key={row.rowKey}
                      row={row}
                      table={table}
                      columns={columns}
                      onCreateTest={onCreateTest}
                    />
                  ))}
                </Table.Tbody>
              </Table>
            </Table.ScrollContainer>
          </Stack>
        );
      })}
    </Stack>
  );
}
