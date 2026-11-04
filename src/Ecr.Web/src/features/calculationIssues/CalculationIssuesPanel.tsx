import { useMemo, type JSX } from 'react';
import { Alert, Anchor, Button, Collapse, Group, List, Text } from '@mantine/core';
import { useDisclosure } from '@mantine/hooks';
import type { DocumentTableDto } from '@/api/types';
import { tableUrlKey } from '@/features/grid/tableTreeModel';
import { t } from '@/shared/i18n';
import { localized } from '@/shared/i18n/localized';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { useUrlState } from '@/shared/ui/useUrlState';
import { issuesOfSheet } from './calculationIssuesModel';
import { isNotFound, useCalculationIssues } from './useCalculationIssues';

export interface CalculationIssuesPanelProps {
  readonly documentId: number;
  readonly periodKey: number;
  /** Копія активного аркуша; `null` - оригінал. */
  readonly sheetCopyId: number | null;
  /** Таблиці активного аркуша: з них береться назва, і вибір таблиці лише серед них. */
  readonly tables: readonly DocumentTableDto[];
}

/**
 * Згортна панель попередження над таблицями: рядки, яких розрахунок не охопив жодним правилом методології (AN-69).
 *
 * Показується лише коли в активному аркуші є проблеми; невидимий документ (`404`) - мовчки нічого, інша
 * відмова читання - стандартне `ErrorAlert`. Клік по рядку вибирає таблицю (`?table=`, як дерево таблиць);
 * прокрутки до самого рядка немає - ключа рядка в сітці для цього не вистачає.
 *
 * Тон `warning` макета (`docs/design/hybrid/KIT.md`, `Banner`): це увага, а не помилка введення.
 */
export function CalculationIssuesPanel({
  documentId,
  periodKey,
  sheetCopyId,
  tables,
}: CalculationIssuesPanelProps): JSX.Element | null {
  const query = useCalculationIssues(documentId, periodKey, sheetCopyId);
  const [, setTableKey] = useUrlState('table');
  const [opened, { toggle }] = useDisclosure(false);

  const issues = useMemo(() => issuesOfSheet(query.data, tables, sheetCopyId), [query.data, tables, sheetCopyId]);

  if (query.isError) {
    if (isNotFound(query.error)) return null;

    return <ErrorAlert error={query.error} onRetry={() => void query.refetch()} />;
  }


  if (issues.length === 0) return null;

  const listId = `calculation-issues-${String(documentId)}`;

  return (
    <Alert color="yellow" variant="light" data-testid="calculation-issues-panel">
      <Group justify="space-between" wrap="nowrap" gap="sm">
        <Text size="sm" fw={600} role="status" data-testid="calculation-issues-title">
          {t('calculationIssues.title', { count: issues.length })}
        </Text>
        <Button
          variant="subtle"
          size="compact-sm"
          aria-expanded={opened}
          aria-controls={listId}
          onClick={toggle}
          data-testid="calculation-issues-toggle"
        >
          {opened ? t('calculationIssues.hide') : t('calculationIssues.show')}
        </Button>
      </Group>

      <Collapse in={opened} id={listId}>
        <Text size="sm" mt="xs">
          {t('calculationIssues.hint')}
        </Text>
        <List size="sm" mt="xs" aria-label={t('calculationIssues.listLabel')}>
          {issues.map((issue) => {
            const table = tables.find((candidate) => candidate.tableCode === issue.tableCode);
            const name = table === undefined ? issue.tableCode : localized(table.tableNameL10n) || issue.tableCode;
            const text = t('calculationIssues.item', {
              table: name,
              row: issue.rowNumber,
              methodology: issue.methodologyCode ?? t('calculationIssues.methodologyUnknown'),
            });

            return (
              <List.Item key={`${issue.tableCode}:${issue.rowKey}`} data-testid="calculation-issue-item">
                {table === undefined ? (
                  <Text size="sm" span>
                    {text}
                  </Text>
                ) : (
                  <Anchor
                    component="button"
                    type="button"
                    size="sm"
                    onClick={() => {
                      setTableKey(tableUrlKey(table, tables));
                    }}
                  >
                    {text}
                  </Anchor>
                )}
              </List.Item>
            );
          })}
        </List>
      </Collapse>
    </Alert>
  );
}
