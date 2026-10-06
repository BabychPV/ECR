import { Suspense, lazy, type JSX, type ReactNode } from 'react';
import { Button, Code, Group, Skeleton, Table, Tabs, Text } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import { listValidationRules, validationRulesKey } from '@/features/templates/validationRuleApi';
import { rowModeLabel } from '@/features/templates/enumLabels';
import { localized } from '@/shared/i18n/localized';
import { t } from '@/shared/i18n';
import { formulasOf, type CtorTab, type FormulaTarget, type LocatedTable } from './ctorModel';
import { sheetNameOf, tableNameOf } from './CtorTree';

// ⚠ Обидві вкладки — окремі чанки (бюджет маршруту 250 КБ, `D-132`): той самий модуль
// уже лінивий у діалогах сторінки, тож другого коду в бандлі не з'являється.
const ValidationRuleList = lazy(async () => ({
  default: (await import('@/features/templates/ValidationRuleList')).ValidationRuleList,
}));
const TablePreview = lazy(async () => ({
  default: (await import('@/features/templates/TablePreview')).TablePreview,
}));

export interface TableWorkspaceProps {
  readonly templateVersionId: number;
  readonly located: LocatedTable;
  readonly tab: CtorTab;
  readonly onTab: (tab: CtorTab) => void;
  /** Перемикач дерева (вузький екран) — ліворуч у шапці робочої області, як у макеті. */
  readonly treeToggle: ReactNode;
  /** Вкладка «Columns»: `ColumnsTable` сторінки. */
  readonly columns: ReactNode;
  /** Вкладка «Rows»: `RowsTable` сторінки; `null` — таблиця динамічна. */
  readonly rows: ReactNode;
  readonly canEditStructure: boolean;
  readonly deleting: boolean;
  readonly deletingRuleCode: string | null;
  readonly onProperties: () => void;
  readonly onConditionalFormat: () => void;
  readonly onDelete: () => void;
  readonly onAddColumn: () => void;
  readonly onFormula: (target: FormulaTarget) => void;
  readonly onAddRule: () => void;
  readonly onDeleteRule: (code: string) => void;
}

/**
 * Робоча область вибраної таблиці (`UI-36`): шапка «1.1 Назва · аркуш» з діями
 * таблиці й вкладки Columns / Rows / Formulas / Validation rules / Preview
 * (макет `screens-templates.js`, `ctor-columns` … `ctor-preview`).
 *
 * ⚠ Монтується ОДНА таблиця: на версії з 91 таблицею й ~3000 колонок дерево
 * плюс одна таблиця замінює стос згорток (`LazyTableSlots` тут більше не потрібен).
 */
export function TableWorkspace(props: TableWorkspaceProps): JSX.Element {
  const { templateVersionId, located, tab, onTab, canEditStructure } = props;
  const { table, sheet, no } = located;
  const formulas = formulasOf(table);

  // Лічильник правил — лише коли перелік уже завантажено (відкрита вкладка або кеш):
  // окремий запит на кожен вибір таблиці заради числа не потрібен.
  const rules = useQuery({
    queryKey: validationRulesKey(templateVersionId, table.id),
    queryFn: () => listValidationRules(templateVersionId, table.id),
    enabled: false,
  });

  const count = (n: number | undefined): ReactNode =>
    n === undefined ? null : (
      <Text span size="xs" c="dimmed" ml="xs" ff="monospace">
        {n}
      </Text>
    );

  return (
    <section aria-labelledby="ctor-table-title" data-testid="ctor-table" data-table-id={table.id}>
      <div className="ecr-ctor-pane-h">
        {props.treeToggle}
        <span className="ecr-ctor-chip">{no}</span>
        <h2 id="ctor-table-title">{tableNameOf(table)}</h2>
        <span className="ecr-ctor-meta">
          {sheetNameOf(sheet)} · {table.code} · {rowModeLabel(table.rowMode)}
        </span>
        {canEditStructure && (
          <div className="ecr-ctor-actions">
            <Button size="xs" variant="subtle" onClick={props.onProperties}>
              {t('ctor.tableProperties')}
            </Button>
            <Button size="xs" variant="subtle" onClick={props.onConditionalFormat}>
              {t('conditionalFormat.title')}
            </Button>
            <Button size="xs" variant="subtle" color="statusError" loading={props.deleting} onClick={props.onDelete}>
              {t('tableDef.delete')}
            </Button>
          </div>
        )}
      </div>

      <div className="ecr-ctor-body">
        <Tabs value={tab} onChange={(value) => onTab((value ?? 'columns') as CtorTab)} keepMounted={false}>
          <Tabs.List mb="sm">
            <Tabs.Tab value="columns">
              {t('ctor.tab.columns')}
              {count(table.columns.length)}
            </Tabs.Tab>
            <Tabs.Tab value="rows">
              {t('ctor.tab.rows')}
              {count(table.rowMode === 'Dynamic' ? undefined : table.rows.length)}
            </Tabs.Tab>
            <Tabs.Tab value="formulas">
              {t('ctor.tab.formulas')}
              {count(formulas.length)}
            </Tabs.Tab>
            <Tabs.Tab value="rules">
              {t('ctor.tab.rules')}
              {count(rules.data?.length)}
            </Tabs.Tab>
            <Tabs.Tab value="preview">{t('ctor.tab.preview')}</Tabs.Tab>
          </Tabs.List>

          <Tabs.Panel value="columns">
            <Group justify="space-between" align="center" wrap="nowrap">
              <p className="ecr-ctor-hint">{t('ctor.columnsHint')}</p>
              {canEditStructure && (
                <Button variant="default" onClick={props.onAddColumn}>
                  {t('columns.add')}
                </Button>
              )}
            </Group>
            {props.columns}
          </Tabs.Panel>

          <Tabs.Panel value="rows">
            {props.rows === null ? <p className="ecr-ctor-hint">{t('ctor.rowsDynamic')}</p> : props.rows}
          </Tabs.Panel>

          <Tabs.Panel value="formulas">
            <p className="ecr-ctor-hint">{t('ctor.formulasHint')}</p>
            {formulas.length === 0 ? (
              <Text size="sm" c="dimmed">
                {t('ctor.formulasEmpty')}
              </Text>
            ) : (
              <Table striped withTableBorder data-testid="ctor-formulas">
                <Table.Thead>
                  <Table.Tr>
                    <Table.Th>{t('ctor.formulaTarget')}</Table.Th>
                    <Table.Th>{t('ctor.formulaExpression')}</Table.Th>
                    <Table.Th />
                  </Table.Tr>
                </Table.Thead>
                <Table.Tbody>
                  {formulas.map((target) => {
                    const key = target.kind === 'Column' ? `c:${target.column.code}` : `r:${target.row.rowKey}`;
                    const name =
                      target.kind === 'Column'
                        ? localized(target.column.headerL10n) || target.column.code
                        : (target.row.label ?? target.row.rowKey);
                    const code =
                      target.kind === 'Column'
                        ? [target.column.code, target.column.unitSymbol].filter(Boolean).join(' · ')
                        : `${t('ctor.formulaRow')} · ${target.row.rowKey}`;

                    return (
                      <Table.Tr key={key}>
                        <Table.Td>
                          <Text size="sm">{name}</Text>
                          <Text size="xs" c="dimmed">
                            {code}
                          </Text>
                        </Table.Td>
                        <Table.Td>
                          <Code>{target.expression}</Code>
                        </Table.Td>
                        <Table.Td>
                          {canEditStructure && (
                            <Group justify="flex-end">
                              <Button size="xs" variant="subtle" onClick={() => props.onFormula(target)}>
                                {t('formulas.edit')}
                              </Button>
                            </Group>
                          )}
                        </Table.Td>
                      </Table.Tr>
                    );
                  })}
                </Table.Tbody>
              </Table>
            )}
          </Tabs.Panel>

          <Tabs.Panel value="rules">
            <Group justify="space-between" align="center" wrap="nowrap">
              <p className="ecr-ctor-hint">{t('ctor.rulesHint')}</p>
              {canEditStructure && (
                <Button variant="default" onClick={props.onAddRule}>
                  {t('ctor.addRule')}
                </Button>
              )}
            </Group>
            <Suspense fallback={<Skeleton height={80} radius="sm" />}>
              <ValidationRuleList
                templateVersionId={templateVersionId}
                tableDefId={table.id}
                disabled={!canEditStructure}
                deletingCode={props.deletingRuleCode}
                onDelete={props.onDeleteRule}
              />
            </Suspense>
          </Tabs.Panel>

          <Tabs.Panel value="preview">
            <Suspense fallback={<Skeleton height={160} radius="sm" />}>
              <TablePreview templateVersionId={templateVersionId} table={table} />
            </Suspense>
          </Tabs.Panel>
        </Tabs>
      </div>
    </section>
  );
}
