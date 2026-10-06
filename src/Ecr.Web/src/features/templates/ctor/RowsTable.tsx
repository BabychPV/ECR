import type { JSX } from 'react';
import { Button, Group, Table, Text } from '@mantine/core';
import { ReorderCell, ReorderableRows } from '@/features/templates/ReorderControls';
import { rowKindLabel } from '@/features/templates/enumLabels';
import { t } from '@/shared/i18n';
import type { TemplateRow, TemplateTable } from './ctorModel';

/**
 * Рядки ФІКСОВАНОЇ таблиці (вкладка «Rows» конструктора, `UI-36`).
 *
 * ⚠ Винесено з `TemplateVersionPage.tsx` без зміни поведінки. Динамічна таблиця
 * (`RowMode.Dynamic`) сюди не потрапляє: домен (`TableDef.AddRow`) відхиляє її
 * рядки шаблоном, вони з'являються під час роботи.
 */
export interface RowsTableProps {
  readonly table: TemplateTable;
  readonly canEditStructure: boolean;
  readonly reorderPending: boolean;
  readonly deletingRowKey: string | null;
  readonly onReorder: (from: number, to: number) => void;
  readonly onAdd: () => void;
  readonly onEdit: (row: TemplateRow) => void;
  readonly onDelete: (row: TemplateRow) => void;
  readonly onFormula: (row: TemplateRow) => void;
  /** Заголовок «Rows» над таблицею; у вкладці його роль бере сама вкладка. */
  readonly withTitle?: boolean;
}

export function RowsTable({
  table,
  canEditStructure,
  reorderPending,
  deletingRowKey,
  onReorder,
  onAdd,
  onEdit,
  onDelete,
  onFormula,
  withTitle = true,
}: RowsTableProps): JSX.Element {
  return (
    <>
      <Group justify={withTitle ? 'space-between' : 'flex-end'} mt="sm">
        {withTitle && (
          <Text fw={600} size="sm" c="dimmed">
            {t('rows.title')}
          </Text>
        )}
        {canEditStructure && (
          <Button size="xs" variant="default" onClick={onAdd}>
            {t('rows.add')}
          </Button>
        )}
      </Group>

      {table.rows.length === 0 ? (
        <Text size="sm" c="dimmed">
          {t('rows.empty')}
        </Text>
      ) : (
        <Table striped withTableBorder mt="xs">
          <Table.Thead>
            <Table.Tr>
              {canEditStructure && <Table.Th>{t('reorder.column')}</Table.Th>}
              <Table.Th>{t('rows.label')}</Table.Th>
              <Table.Th>{t('rows.rowKind')}</Table.Th>
              <Table.Th />
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {/* AN-15: порядок рядків — `RowDef.Ordinal` через `PATCH …/presentation` (`reorder.ts`). */}
            <ReorderableRows items={table.rows} enabled={canEditStructure && !reorderPending} onMove={onReorder}>
              {(row, index, drag) => (
                <Table.Tr key={row.rowKey} {...drag.targetProps(index)}>
                  {canEditStructure && (
                    <Table.Td>
                      <ReorderCell
                        index={index}
                        count={table.rows.length}
                        name={row.label ?? row.rowKey}
                        disabled={reorderPending}
                        onMove={onReorder}
                        drag={drag}
                      />
                    </Table.Td>
                  )}
                  <Table.Td>
                    {row.label ?? row.rowKey}{' '}
                    <Text span c="dimmed">
                      ({row.rowKey})
                    </Text>
                  </Table.Td>
                  <Table.Td>{rowKindLabel(row.rowKind)}</Table.Td>
                  <Table.Td>
                    {canEditStructure && (
                      <Group gap="xs" wrap="nowrap" justify="flex-end">
                        <Button size="xs" variant="subtle" onClick={() => onEdit(row)}>
                          {t('rows.edit')}
                        </Button>
                        <Button
                          size="xs"
                          variant="subtle"
                          color="statusError"
                          loading={deletingRowKey === row.rowKey}
                          onClick={() => onDelete(row)}
                        >
                          {t('rows.delete')}
                        </Button>
                        <Button size="xs" variant="subtle" onClick={() => onFormula(row)}>
                          {t('formulas.edit')}
                        </Button>
                      </Group>
                    )}
                  </Table.Td>
                </Table.Tr>
              )}
            </ReorderableRows>
          </Table.Tbody>
        </Table>
      )}
    </>
  );
}
