import type { JSX } from 'react';
import { ActionIcon, Alert, Select, Stack, Switch, Text, TextInput } from '@mantine/core';
import { t } from '@/shared/i18n';
import type { PermitTypeColumn, PermitTypeTable } from '../api/types';
import { ChoicesFrom } from './ChoicesFrom';
import { ColumnDataTypes, isValidCode, scopeKeyOf, withListScope, type ColumnDataType } from './draftModel';
import type { DraftProblem } from './draftValidate';
import { dataTypeLabel, textOf } from './labels';
import { useUnits } from './lookups';
import './typeEditor.css';

export interface ColumnInspectorProps {
  readonly table: PermitTypeTable;
  readonly column: PermitTypeColumn;
  readonly tables: readonly PermitTypeTable[];
  readonly editable: boolean;
  /** Відмови копії; показуються ті, що стосуються цього стовпця. */
  readonly problems: readonly DraftProblem[];
  readonly onChange: (next: PermitTypeColumn) => void;
  readonly onClose: () => void;
}

function withName(column: PermitTypeColumn, lang: string, value: string): PermitTypeColumn {
  const names = { ...column.nameL10n };
  if (value === '') delete names[lang];
  else names[lang] = value;
  return { ...column, nameL10n: names };
}

/**
 * Інспектор стовпця (мокап: `fillAside`): назви EN/RU, код (лише читання), тип, джерело значень,
 * одиниця, група, «обовʼязковий», «частина ключа». Правка одразу йде в робочу копію; збереження —
 * на сервер окремо (`useTypeEditor`). Ключовий стовпець завжди обовʼязковий — перемикач
 * «Required» тоді вимкнений і показує «так».
 */
export function ColumnInspector({ table, column, tables, editable, problems, onChange, onClose }: ColumnInspectorProps): JSX.Element {
  const units = useUnits();
  const mine = problems.filter((problem) => problem.tableCode === table.code && problem.columnCode === column.code);
  const otherTables = tables.filter((other) => other.code.toLowerCase() !== table.code.toLowerCase());
  const numeric = column.dataType === 'Int' || column.dataType === 'Decimal';
  const groupInvalid = column.group !== null && !isValidCode(column.group);

  const changeType = (value: string | null): void => {
    if (value === null || value === column.dataType) return;
    const next = value as ColumnDataType;
    const base = { ...column, dataType: next };
    onChange(next === 'Lookup' ? withListScope(base, scopeKeyOf(column) ?? 'shared', otherTables.map((o) => o.code)) : base);
  };

  return (
    <aside className="pt-aside" aria-label={t('permits.editor.inspector.aria')} data-testid="column-inspector">
      <div className="pt-pane-h">
        <h2>{textOf(column.nameL10n) || column.code}</h2>
        <ActionIcon variant="subtle" color="gray" aria-label={t('permits.editor.inspector.close')} onClick={onClose}>
          <span aria-hidden="true">×</span>
        </ActionIcon>
      </div>
      <Stack className="pt-inspect" gap="md">
        {!editable && (
          <Text size="sm" c="dimmed">
            {t('permits.editor.inspector.readOnly')}
          </Text>
        )}
        {mine.length > 0 && (
          <Alert color="statusWarning" role="status">
            <ul className="pt-list">
              {mine.map((problem, index) => (
                <li key={`${problem.messageKey}-${index}`}>{t(problem.messageKey, problem.params)}</li>
              ))}
            </ul>
          </Alert>
        )}
        <TextInput
          label={t('permits.editor.inspector.nameEn')}
          required
          value={column.nameL10n['en'] ?? ''}
          disabled={!editable}
          onChange={(event) => onChange(withName(column, 'en', event.currentTarget.value))}
        />
        <TextInput
          label={t('permits.editor.inspector.nameRu')}
          description={t('permits.editor.inspector.nameRuHint')}
          value={column.nameL10n['ru'] ?? ''}
          disabled={!editable}
          onChange={(event) => onChange(withName(column, 'ru', event.currentTarget.value))}
        />
        <TextInput
          label={t('permits.editor.inspector.code')}
          description={t('permits.editor.inspector.codeHint')}
          value={column.code}
          readOnly
          styles={{ input: { fontFamily: 'var(--mantine-font-family-monospace)' } }}
        />
        <Select
          label={t('permits.editor.inspector.type')}
          value={column.dataType}
          onChange={changeType}
          disabled={!editable}
          allowDeselect={false}
          data={ColumnDataTypes.map((type) => ({ value: type, label: dataTypeLabel(type) }))}
        />
        {column.dataType === 'Lookup' && (
          <ChoicesFrom column={column} otherTables={otherTables} editable={editable} onChange={onChange} />
        )}
        {numeric && (
          <Select
            label={t('permits.editor.inspector.unit')}
            placeholder={t('permits.editor.inspector.unitNone')}
            value={column.unitId === null ? null : String(column.unitId)}
            onChange={(value) => onChange({ ...column, unitId: value === null ? null : Number(value) })}
            disabled={!editable}
            clearable
            searchable
            data={units.list.map((unit) => ({ value: String(unit.id), label: unit.code }))}
          />
        )}
        <TextInput
          label={t('permits.editor.inspector.group')}
          description={t('permits.editor.inspector.groupHint')}
          value={column.group ?? ''}
          disabled={!editable}
          error={groupInvalid ? t('permits.editor.inspector.groupInvalid') : undefined}
          onChange={(event) => onChange({ ...column, group: event.currentTarget.value === '' ? null : event.currentTarget.value })}
        />
        <Switch
          label={t('permits.editor.inspector.required')}
          description={column.key ? t('permits.editor.inspector.requiredByKey') : undefined}
          checked={column.required}
          disabled={!editable || column.key}
          onChange={(event) => onChange({ ...column, required: event.currentTarget.checked })}
        />
        <Switch
          label={t('permits.editor.inspector.key')}
          description={t('permits.editor.inspector.keyHint')}
          checked={column.key}
          disabled={!editable}
          onChange={(event) => onChange({ ...column, key: event.currentTarget.checked })}
        />
      </Stack>
    </aside>
  );
}
