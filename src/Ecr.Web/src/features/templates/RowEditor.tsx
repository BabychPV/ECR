import type { JSX } from 'react';
import { Alert, Button, Group, NumberInput, Select, Stack, Switch, TextInput } from '@mantine/core';
import { t } from '@/shared/i18n';
import { LocalizedInput } from '@/shared/ui/LocalizedInput';
import { type RowBlocker, type RowDraft, RowKindOptions, whyCannotSaveRow } from './row';

/**
 * Форма рядка фіксованої таблиці — третій вертикальний зріз авторства
 * структури шаблону (`W5.2`), за зразком `SheetEditor.tsx` (`ФВ-2.1`,
 * `W5.0`) і `ColumnEditor.tsx`.
 */
export function RowEditor({
  draft,
  disabled,
  saving,
  onChange,
  onSubmit,
  onCancel,
}: {
  draft: RowDraft;
  disabled: boolean;
  saving: boolean;
  onChange: (next: RowDraft) => void;
  onSubmit: () => void;
  onCancel: () => void;
}): JSX.Element {
  const blocker = whyCannotSaveRow(draft);

  return (
    <Stack gap="sm">
      <TextInput
        label={t('rows.rowKey')}
        description={t('rows.rowKeyHint')}
        value={draft.rowKey}
        disabled={disabled || !draft.isNew}
        onChange={(event) => onChange({ ...draft, rowKey: event.currentTarget.value })}
      />

      <LocalizedInput
        label={t('rows.label')}
        description={t('rows.labelHint')}
        value={draft.labelL10n}
        onChange={(labelL10n) => onChange({ ...draft, labelL10n })}
      />

      <Select
        label={t('rows.rowKind')}
        description={t('rows.rowKindHint')}
        data={RowKindOptions}
        value={draft.rowKind}
        disabled={disabled || !draft.isNew}
        allowDeselect={false}
        onChange={(value) => {
          if (value !== null) onChange({ ...draft, rowKind: value as RowDraft['rowKind'] });
        }}
      />

      <NumberInput
        label={t('rows.ordinal')}
        description={t('rows.ordinalHint')}
        disabled={disabled}
        value={draft.ordinal ?? ''}
        onChange={(value) =>
          onChange({ ...draft, ordinal: typeof value === 'number' ? value : draft.ordinal })
        }
      />

      <TextInput
        label={t('rows.parentRowKey')}
        description={t('rows.parentRowKeyHint')}
        disabled={disabled}
        value={draft.parentRowKey}
        onChange={(event) => onChange({ ...draft, parentRowKey: event.currentTarget.value })}
      />

      <Switch
        label={t('rows.readOnly')}
        description={t('rows.readOnlyHint')}
        disabled={disabled}
        checked={draft.isReadOnly}
        onChange={(event) => onChange({ ...draft, isReadOnly: event.currentTarget.checked })}
      />

      {!draft.hasFullLabel && <Alert color="yellow">{t('rows.partialLabelWarning')}</Alert>}

      {blocker !== null && <Alert color="yellow">{blockerLabel(blocker)}</Alert>}

      <Group>
        <Button variant="default" onClick={onCancel}>
          {t('common.cancel')}
        </Button>
        <Button disabled={disabled || blocker !== null} loading={saving} onClick={onSubmit}>
          {t('rows.save')}
        </Button>
      </Group>
    </Stack>
  );
}

/** Підпис причини, з якої зберегти ще не можна. */
function blockerLabel(blocker: RowBlocker): string {
  switch (blocker) {
    case 'RowKey':
      return t('rows.errRowKey');
    case 'Label':
      return t('rows.errLabel');
    default:
      return blocker;
  }
}
