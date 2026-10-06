import type { JSX, ReactNode } from 'react';
import { Anchor, Button, Group, Table, Text } from '@mantine/core';
import type { TemplateStructureDto } from '@/api/types';
import { t } from '@/shared/i18n';
import { formulasOf, sortedSheets, sortedTables, type TemplateSheet } from './ctorModel';
import { sheetNameOf, tableNameOf } from './CtorTree';

/**
 * Вузли дерева без таблиці (`UI-36`, макет `ctor-root` і `ctor-sheet`): огляд
 * версії (поля шапки + аркуші) і огляд аркуша (таблиці в порядку оператора).
 *
 * ⚠ Рядок переліку — кнопка-посилання на вузол дерева, а не мертвий текст:
 * з огляду людина переходить туди, де править.
 */

export function VersionOverview({
  structure,
  version,
  treeToggle,
  headerFields,
  canEdit,
  onAddSheet,
  onOpenSheet,
}: {
  readonly structure: TemplateStructureDto;
  readonly version: string | undefined;
  readonly treeToggle: ReactNode;
  readonly headerFields: ReactNode;
  readonly canEdit: boolean;
  readonly onAddSheet: () => void;
  readonly onOpenSheet: (code: string) => void;
}): JSX.Element {
  const sheets = sortedSheets(structure);
  const tables = sheets.reduce((sum, sheet) => sum + sheet.tables.length, 0);

  return (
    <section aria-labelledby="ctor-root-title" data-testid="ctor-root">
      <div className="ecr-ctor-pane-h">
        {treeToggle}
        <h2 id="ctor-root-title">{version === undefined ? t('version.title') : t('ctor.versionNode', { version })}</h2>
        <span className="ecr-ctor-meta">{t('ctor.versionMeta', { sheets: sheets.length, tables })}</span>
      </div>
      <div className="ecr-ctor-body">
        {headerFields}

        <Group justify="space-between" mt="md" mb="xs">
          <Group gap="xs">
            <Text fw={600}>{t('ctor.sheets')}</Text>
            <Text size="xs" c="dimmed">
              {t('ctor.sheetsHint')}
            </Text>
          </Group>
          {canEdit && (
            <Button variant="default" onClick={onAddSheet}>
              {t('sheets.add')}
            </Button>
          )}
        </Group>
        <Table striped withTableBorder>
          <Table.Thead>
            <Table.Tr>
              <Table.Th>{t('ctor.sheet')}</Table.Th>
              <Table.Th ta="right">{t('ctor.tables')}</Table.Th>
              <Table.Th>{t('ctor.visibility')}</Table.Th>
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {sheets.map((sheet) => (
              <Table.Tr key={sheet.id}>
                <Table.Td>
                  <Anchor component="button" type="button" size="sm" onClick={() => onOpenSheet(sheet.code)}>
                    {sheetNameOf(sheet)}
                  </Anchor>
                  <Text size="xs" c="dimmed">
                    {sheet.code}
                  </Text>
                </Table.Td>
                <Table.Td ta="right" ff="monospace">
                  {sheet.tables.length}
                </Table.Td>
                <Table.Td>{sheet.isVisible ? t('ctor.visible') : t('version.hidden')}</Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      </div>
    </section>
  );
}

export function SheetOverview({
  sheet,
  sheetNo,
  sheetCount,
  treeToggle,
  canEdit,
  deleting,
  onEdit,
  onDelete,
  onAddTable,
  onOpenTable,
}: {
  readonly sheet: TemplateSheet;
  readonly sheetNo: number;
  readonly sheetCount: number;
  readonly treeToggle: ReactNode;
  readonly canEdit: boolean;
  readonly deleting: boolean;
  readonly onEdit: () => void;
  readonly onDelete: () => void;
  readonly onAddTable: () => void;
  readonly onOpenTable: (id: number) => void;
}): JSX.Element {
  const tables = sortedTables(sheet);

  return (
    <section aria-labelledby="ctor-sheet-title" data-testid="ctor-sheet" data-sheet-code={sheet.code}>
      <div className="ecr-ctor-pane-h">
        {treeToggle}
        <span className="ecr-ctor-chip">{sheet.code}</span>
        <h2 id="ctor-sheet-title">{sheetNameOf(sheet)}</h2>
        <span className="ecr-ctor-meta">
          {t('ctor.sheetMeta', { no: sheetNo, total: sheetCount, count: tables.length })}
          {!sheet.isVisible && ` · ${t('version.hidden')}`}
        </span>
        {canEdit && (
          <div className="ecr-ctor-actions">
            <Button size="xs" variant="subtle" onClick={onEdit}>
              {t('sheets.edit')}
            </Button>
            <Button size="xs" variant="subtle" color="statusError" loading={deleting} onClick={onDelete}>
              {t('sheets.delete')}
            </Button>
          </div>
        )}
      </div>
      <div className="ecr-ctor-body">
        <Group justify="space-between" mb="xs">
          <Group gap="xs">
            <Text fw={600}>{t('ctor.tables')}</Text>
            <Text size="xs" c="dimmed">
              {t('ctor.tablesHint')}
            </Text>
          </Group>
          {canEdit && (
            <Button variant="default" onClick={onAddTable}>
              {t('tableDef.add')}
            </Button>
          )}
        </Group>
        {tables.length === 0 ? (
          <Text size="sm" c="dimmed">
            {t('ctor.sheetEmpty')}
          </Text>
        ) : (
          <Table striped withTableBorder>
            <Table.Thead>
              <Table.Tr>
                <Table.Th>{t('ctor.no')}</Table.Th>
                <Table.Th>{t('ctor.table')}</Table.Th>
                <Table.Th ta="right">{t('ctor.tab.columns')}</Table.Th>
                <Table.Th ta="right">{t('ctor.tab.formulas')}</Table.Th>
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {tables.map((table, index) => (
                <Table.Tr key={table.id}>
                  <Table.Td ff="monospace">{`${String(sheetNo)}.${String(index + 1)}`}</Table.Td>
                  <Table.Td>
                    <Anchor component="button" type="button" size="sm" onClick={() => onOpenTable(table.id)}>
                      {tableNameOf(table)}
                    </Anchor>
                  </Table.Td>
                  <Table.Td ta="right" ff="monospace">
                    {table.columns.length}
                  </Table.Td>
                  <Table.Td ta="right" ff="monospace">
                    {formulasOf(table).length}
                  </Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        )}
      </div>
    </section>
  );
}
