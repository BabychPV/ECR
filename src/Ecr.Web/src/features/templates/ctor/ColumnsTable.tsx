import type { JSX } from 'react';
import { Badge, Button, Group, Table, Text } from '@mantine/core';
import type { TemplateColumnDto } from '@/api/types';
import { ReorderCell, ReorderableRows } from '@/features/templates/ReorderControls';
import { dataTypeLabel } from '@/features/templates/enumLabels';
import { localized } from '@/shared/i18n/localized';
import { t } from '@/shared/i18n';
import type { TemplateTable } from './ctorModel';

/**
 * Колонки однієї таблиці версії шаблону (вкладка «Columns» конструктора, `UI-36`).
 *
 * ⚠ Винесено з `TemplateVersionPage.tsx` без зміни поведінки: стан і мутації
 * лишаються на сторінці, тут — лише розмітка рядка й виклики назад.
 */
export interface ColumnsTableProps {
  readonly table: TemplateTable;
  /** `Template.Edit`: перестановка й «Appearance» законні й в опублікованій версії (`ФВ-7.2`). */
  readonly canEditPresentation: boolean;
  /** `Template.Edit` І `isEditable` версії: структура (правка, видалення, формула). */
  readonly canEditStructure: boolean;
  readonly reorderPending: boolean;
  readonly deletingCode: string | null;
  readonly onReorder: (from: number, to: number) => void;
  readonly onUsage: (column: TemplateColumnDto) => void;
  readonly onPresentation: (column: TemplateColumnDto) => void;
  readonly onEdit: (column: TemplateColumnDto) => void;
  readonly onDelete: (column: TemplateColumnDto) => void;
  readonly onFormula: (column: TemplateColumnDto) => void;
}

export function ColumnsTable({
  table,
  canEditPresentation,
  canEditStructure,
  reorderPending,
  deletingCode,
  onReorder,
  onUsage,
  onPresentation,
  onEdit,
  onDelete,
  onFormula,
}: ColumnsTableProps): JSX.Element {
  return (
    <Table striped withTableBorder mt="xs">
      <Table.Thead>
        <Table.Tr>
          {canEditPresentation && <Table.Th>{t('reorder.column')}</Table.Th>}
          <Table.Th>{t('version.column')}</Table.Th>
          <Table.Th>{t('version.type')}</Table.Th>
          <Table.Th>{t('version.unit')}</Table.Th>
          <Table.Th />
        </Table.Tr>
      </Table.Thead>
      <Table.Tbody>
        <ReorderableRows items={table.columns} enabled={canEditPresentation && !reorderPending} onMove={onReorder}>
          {(column, index, drag) => (
            <Table.Tr key={column.id} {...drag.targetProps(index)}>
              {canEditPresentation && (
                <Table.Td>
                  <ReorderCell
                    index={index}
                    count={table.columns.length}
                    name={localized(column.headerL10n) || column.code}
                    disabled={reorderPending}
                    onMove={onReorder}
                    drag={drag}
                  />
                </Table.Td>
              )}
              <Table.Td>
                {localized(column.headerL10n) || column.code}{' '}
                <Text span c="dimmed">
                  ({column.code})
                </Text>
                {column.isHidden && (
                  <Badge ml="xs" size="xs" variant="outline">
                    {t('version.hidden')}
                  </Badge>
                )}
              </Table.Td>
              <Table.Td>
                {dataTypeLabel(column.dataType)}
                {column.isReadOnly && (
                  <Badge ml="xs" size="xs" variant="light">
                    {t('version.readOnly')}
                  </Badge>
                )}
              </Table.Td>
              <Table.Td>{column.unitSymbol ?? '—'}</Table.Td>
              <Table.Td>
                <Group gap="xs" wrap="nowrap" justify="flex-end">
                  {/* ⛔ ФВ-8.14: кнопка є для КОЖНОЇ колонки завжди — `total: 0` теж відповідь. */}
                  <Button size="xs" variant="subtle" data-column-usage="trigger" onClick={() => onUsage(column)}>
                    {t('registries.tabUsage')}
                  </Button>
                  {/* ⚠ Правка тут не потребує нової версії: підпис, порядок, формат і
                      видимість — презентаційний шар (`ФВ-7.2`). */}
                  {canEditPresentation && (
                    <Button size="xs" variant="subtle" onClick={() => onPresentation(column)}>
                      {t('version.presentation')}
                    </Button>
                  )}
                  {canEditStructure && (
                    <>
                      <Button size="xs" variant="subtle" onClick={() => onEdit(column)}>
                        {t('columns.edit')}
                      </Button>
                      <Button
                        size="xs"
                        variant="subtle"
                        color="statusError"
                        loading={deletingCode === column.code}
                        onClick={() => onDelete(column)}
                      >
                        {t('columns.delete')}
                      </Button>
                      <Button size="xs" variant="subtle" onClick={() => onFormula(column)}>
                        {t('formulas.edit')}
                      </Button>
                    </>
                  )}
                </Group>
              </Table.Td>
            </Table.Tr>
          )}
        </ReorderableRows>
      </Table.Tbody>
    </Table>
  );
}
