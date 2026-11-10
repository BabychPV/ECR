import { lazy, Suspense, useState, type JSX } from 'react';
import { Badge, Button, Checkbox, Group, Paper, Table, Text } from '@mantine/core';
import type { TemplateColumnDto } from '@/api/types';
import { columnOffersFormula } from '@/features/templates/column';
import { ReorderCell, ReorderableRows } from '@/features/templates/ReorderControls';
import { dataTypeLabel } from '@/features/templates/enumLabels';
import { localized } from '@/shared/i18n/localized';
import { t } from '@/shared/i18n';
import { groupPath } from './columnGroupPath';
import type { TemplateTable } from './ctorModel';

/**
 * ⚠ Лінивий чанк (бюджет маршруту D-132, 250 КБ gzip): діалог потрібен лише після «Edit selected…», а статичний
 * імпорт додавав його до графа `TemplateVersionPage` (251.5 КБ). Той самий прийом, що в редакторах сторінки.
 */
const BulkGroupDialog = lazy(async () => ({
  default: (await import('./BulkGroupDialog')).BulkGroupDialog,
}));

/**
 * Колонки однієї таблиці версії шаблону (вкладка «Columns» конструктора, `UI-36`).
 *
 * ⚠ Винесено з `TemplateVersionPage.tsx` без зміни поведінки: стан і мутації
 * лишаються на сторінці, тут — лише розмітка рядка й виклики назад.
 *
 * ✎ 10.11 (план PERMITES §3, макет `?dialog=edit-selected`, KIT §6.5.1): колонка «Group» (шлях групи
 * заголовка) і вибір рядків із панеллю «N selected · Edit selected… · Clear selection». Вибір — локальний
 * стан таблиці; масова правка — `BulkGroupDialog`, один PATCH «все або нічого».
 */
export interface ColumnsTableProps {
  readonly table: TemplateTable;
  /** Версія і її поточна ревізія вигляду — для масової правки з `expectedPresentationRevision`. */
  readonly templateVersionId: number;
  readonly presentationRevision: number;
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
  templateVersionId,
  presentationRevision,
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
  const [picked, setPicked] = useState<ReadonlySet<number>>(new Set());
  const [bulkOpen, setBulkOpen] = useState(false);

  // Після перечитування структури зниклі колонки випадають із вибору самі.
  const selected = table.columns.filter((column) => picked.has(column.id));
  const allPicked = table.columns.length > 0 && selected.length === table.columns.length;

  const toggle = (id: number, on: boolean): void => {
    setPicked((previous) => {
      const next = new Set(previous);
      if (on) next.add(id);
      else next.delete(id);
      return next;
    });
  };

  const clear = (): void => setPicked(new Set());

  return (
    <>
      {canEditPresentation && selected.length > 0 && (
        <Paper withBorder p="xs" mt="xs" role="toolbar" aria-label={t('columns.bulkToolbar')} data-columns-bulk="bar">
          <Group gap="sm" wrap="wrap">
            <Text fw={600} size="sm" role="status" aria-live="polite">
              {t('columns.selectedCount', { count: selected.length })}
            </Text>
            <Button size="xs" onClick={() => setBulkOpen(true)} data-columns-bulk="edit">
              {t('columns.editSelected')}
            </Button>
            <Button size="xs" variant="subtle" onClick={clear}>
              {t('columns.clearSelection')}
            </Button>
          </Group>
        </Paper>
      )}

      <Table striped withTableBorder mt="xs">
        <Table.Thead>
          <Table.Tr>
            {canEditPresentation && (
              <Table.Th w={36}>
                <Checkbox
                  aria-label={t('columns.selectAll')}
                  checked={allPicked}
                  indeterminate={selected.length > 0 && !allPicked}
                  onChange={(event) => setPicked(event.currentTarget.checked ? new Set(table.columns.map((c) => c.id)) : new Set())}
                />
              </Table.Th>
            )}
            {canEditPresentation && <Table.Th>{t('reorder.column')}</Table.Th>}
            <Table.Th>{t('version.column')}</Table.Th>
            <Table.Th>{t('version.type')}</Table.Th>
            <Table.Th>{t('version.unit')}</Table.Th>
            <Table.Th>{t('colgroups.field')}</Table.Th>
            <Table.Th />
          </Table.Tr>
        </Table.Thead>
        <Table.Tbody>
          <ReorderableRows items={table.columns} enabled={canEditPresentation && !reorderPending} onMove={onReorder}>
            {(column, index, drag) => (
              <Table.Tr key={column.id} {...drag.targetProps(index)}>
                {canEditPresentation && (
                  <Table.Td>
                    <Checkbox
                      aria-label={t('columns.selectOne', { name: localized(column.headerL10n) || column.code })}
                      checked={picked.has(column.id)}
                      onChange={(event) => toggle(column.id, event.currentTarget.checked)}
                    />
                  </Table.Td>
                )}
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
                <Table.Td data-column-group={column.code}>
                  {column.headerGroupId == null ? (
                    '—'
                  ) : (
                    <Text span size="sm" c="dimmed">
                      {groupPath(table.headerGroups, column.headerGroupId)}
                    </Text>
                  )}
                </Table.Td>
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
                        {/* p3-1110: формула колонки — лише на обчислюваній колонці (ColumnDef.IsComputed, сервер
                            відмовляє ECR-TMPL-4227) або щоб прибрати вже наявну. */}
                        {columnOffersFormula(column) && (
                          <Button size="xs" variant="subtle" onClick={() => onFormula(column)} data-column-formula="trigger">
                            {t('formulas.edit')}
                          </Button>
                        )}
                      </>
                    )}
                  </Group>
                </Table.Td>
              </Table.Tr>
            )}
          </ReorderableRows>
        </Table.Tbody>
      </Table>

      {bulkOpen && (
        <Suspense fallback={null}>
        <BulkGroupDialog
          templateVersionId={templateVersionId}
          presentationRevision={presentationRevision}
          columns={selected}
          headerGroups={table.headerGroups}
          opened={bulkOpen}
          onClose={() => setBulkOpen(false)}
          onApplied={clear}
        />
        </Suspense>
      )}
    </>
  );
}
