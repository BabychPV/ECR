import { useMemo, type JSX } from 'react';
import { Button, Group, Stack, Text } from '@mantine/core';
import type { PermitType } from '@/features/permits/api/types';
import { t } from '@/shared/i18n';
import { CodeText } from '@/shared/ui/CodeText';
import { DataTable } from '@/shared/ui/DataTable/DataTable';
import type { DataTableColumn } from '@/shared/ui/DataTable/types';
import { pickName } from '../dialogs/names';
import { flattenRules, type RuleRow } from './ruleOps';

/**
 * Вкладка «Checks» конструктора типу (мокап §4, `rulesTab`; Permits, PM-08c): вступне речення,
 * таблиця перевірок [Check, Applies to, Rule, If it fails] і кнопка «Add check».
 *
 * Самодостатня: бачить лише `type`; відкриває діалог не сама, а через `onAddCheck` (хост
 * ставить `?dialog=add-rule`). Порожній стан — «No checks yet.».
 *
 * ⚠ `canEdit={false}` ховає «Add check» (тип опублікований і без відкритої чернетки: правити
 * можна лише в новій версії).
 */
export interface TypeChecksTabProps {
  readonly type: PermitType;
  readonly onAddCheck: () => void;
  readonly canEdit?: boolean | undefined;
}

export function TypeChecksTab({ type, onAddCheck, canEdit = true }: TypeChecksTabProps): JSX.Element {
  const rows = useMemo(() => flattenRules(type), [type]);

  const columns: readonly DataTableColumn<RuleRow>[] = [
    {
      key: 'name',
      label: t('permits.editor.checks.colCheck'),
      sortValue: (row) => pickName(row.rule.nameL10n),
      render: (row) => pickName(row.rule.nameL10n),
    },
    {
      key: 'scope',
      label: t('permits.editor.checks.colAppliesTo'),
      sortValue: (row) => (row.scope.kind === 'permit' ? '' : row.scope.tableName),
      render: (row) =>
        row.scope.kind === 'permit' ? t('permits.editor.checks.wholePermit') : row.scope.tableName,
    },
    {
      key: 'expression',
      label: t('permits.editor.checks.colRule'),
      sortable: false,
      render: (row) => <CodeText>{row.rule.expression}</CodeText>,
    },
    {
      key: 'severity',
      label: t('permits.editor.checks.colIfFails'),
      sortValue: (row) => row.rule.severity,
      render: (row) =>
        row.rule.severity === 'Block'
          ? t('permits.editor.checks.blocks')
          : t('permits.editor.checks.warns'),
    },
  ];

  return (
    <Stack gap="md">
      <Text size="sm" c="dimmed">
        {t('permits.editor.checks.intro')}
      </Text>

      <DataTable<RuleRow>
        columns={columns}
        rows={rows}
        rowKey={(row) => row.key}
        emptyTitle={t('permits.editor.checks.empty')}
        emptyHeadingOrder={2}
        caption={t('permits.editor.checks.caption')}
      />

      {canEdit ? (
        <Group>
          <Button variant="default" onClick={onAddCheck}>
            {t('permits.editor.checks.add')}
          </Button>
        </Group>
      ) : null}
    </Stack>
  );
}
