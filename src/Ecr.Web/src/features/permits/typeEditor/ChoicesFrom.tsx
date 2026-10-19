import { useEffect, useState, type JSX } from 'react';
import { Select, Stack, Text, Textarea } from '@mantine/core';
import { localized } from '@/shared/i18n/localized';
import { t } from '@/shared/i18n';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import type { PermitTypeColumn, PermitTypeTable } from '../api/types';
import { scopeKeyOf, withListScope, type ListScopeKey } from './draftModel';
import { textOf } from './labels';
import { useSharedRegistries } from './lookups';

export interface ChoicesFromProps {
  readonly column: PermitTypeColumn;
  /** Таблиці типу, окрім власної: з них можна взяти значення («table of the same permit»). */
  readonly otherTables: readonly PermitTypeTable[];
  readonly editable: boolean;
  readonly onChange: (next: PermitTypeColumn) => void;
}

/** Значення списку «лише для типу» з тексту: по одному в рядку, порожні відкинуто. */
export function choicesFromText(text: string): string[] {
  return text
    .split(/\r?\n/)
    .map((line) => line.trim())
    .filter((line) => line !== '');
}

/**
 * Блок «Choices come from» інспектора (мокап: `choicesBlock`): три області списку й поле під
 * вибрану. Модель — `features/permits/lib/pickerScope.ts` (`shared | type | permit`); у контракті
 * їм відповідають `SharedRegistry | TypeOnly | SamePermitTable` (`ListScopes`).
 */
export function ChoicesFrom({ column, otherTables, editable, onChange }: ChoicesFromProps): JSX.Element {
  const scope = scopeKeyOf(column) ?? 'shared';
  const registries = useSharedRegistries(scope === 'shared');
  const [text, setText] = useState((column.choices ?? []).join('\n'));

  // Інший стовпець чи інша область — текст береться з нього, а не з того, що набрано в попередньому.
  useEffect(() => {
    setText((column.choices ?? []).join('\n'));
    // Лише при зміні стовпця або області: інакше кожне натискання клавіші перетирало б набране (порожній рядок).
  }, [column.code, column.listScope]);

  const switchTo = (next: string | null): void => {
    if (next === null || next === scope) return;
    onChange(withListScope(column, next as ListScopeKey, otherTables.map((table) => table.code)));
  };

  const registryData = (registries.data ?? [])
    .map((registry) => ({ value: registry.code, label: `${localized(registry.nameL10n) || registry.code} (${registry.code})` }))
    .sort((a, b) => a.label.localeCompare(b.label));
  if (column.listRegistryCode !== null && !registryData.some((item) => item.value === column.listRegistryCode)) {
    registryData.unshift({ value: column.listRegistryCode, label: column.listRegistryCode });
  }

  return (
    <Stack gap="md">
      <Select
        label={t('permits.editor.choices.from')}
        value={scope}
        onChange={switchTo}
        disabled={!editable}
        allowDeselect={false}
        data={[
          { value: 'shared', label: t('permits.editor.choices.shared') },
          { value: 'type', label: t('permits.editor.choices.type') },
          { value: 'permit', label: t('permits.editor.choices.permit') },
        ]}
      />
      {scope === 'shared' && (
        <>
          <Select
            label={t('permits.editor.choices.registry')}
            description={t('permits.editor.choices.registryHint')}
            value={column.listRegistryCode}
            onChange={(value) => onChange({ ...column, listRegistryCode: value })}
            disabled={!editable}
            searchable
            nothingFoundMessage={t('permits.editor.choices.noRegistries')}
            data={registryData}
          />
          {registries.isError && <ErrorAlert error={registries.error} onRetry={() => void registries.refetch()} />}
        </>
      )}
      {scope === 'type' && (
        <Textarea
          label={t('permits.editor.choices.values')}
          description={t('permits.editor.choices.valuesHint')}
          autosize
          minRows={5}
          value={text}
          disabled={!editable}
          onChange={(event) => {
            setText(event.currentTarget.value);
            onChange({ ...column, choices: choicesFromText(event.currentTarget.value) });
          }}
        />
      )}
      {scope === 'permit' &&
        (otherTables.length === 0 ? (
          <Text size="sm" c="dimmed">
            {t('permits.editor.choices.noOtherTables')}
          </Text>
        ) : (
          <Select
            label={t('permits.editor.choices.table')}
            description={t('permits.editor.choices.tableHint')}
            value={column.siblingTableCode}
            onChange={(value) => onChange({ ...column, siblingTableCode: value })}
            disabled={!editable}
            allowDeselect={false}
            data={otherTables.map((table) => ({ value: table.code, label: textOf(table.nameL10n) || table.code }))}
          />
        ))}
    </Stack>
  );
}
