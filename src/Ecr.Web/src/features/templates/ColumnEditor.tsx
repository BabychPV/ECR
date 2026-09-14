import { useMemo, type JSX } from 'react';
import { Alert, Button, Group, NumberInput, Select, Stack, Switch, TextInput } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import { queryKeys } from '@/api/queryKeys';
import type { RegistryDefDto } from '@/api/types';
import { t } from '@/shared/i18n';
import { localized } from '@/shared/i18n/localized';
import { LocalizedInput } from '@/shared/ui/LocalizedInput';
import {
  type ColumnBlocker,
  type ColumnDraft,
  EditableDataTypes,
  whyCannotSaveColumn,
} from './column';

/**
 * Форма колонки — другий вертикальний зріз авторства структури шаблону
 * (`W5.2`), за зразком `SheetEditor.tsx` (`ФВ-2.1`, `W5.0`).
 *
 * ⚠ Форма нічого не вирішує про стан версії — так само, як `SheetEditor`:
 * `disabled` лише заважає заповнювати те, що однаково не збережеться
 * (`isEditable` рахує сервер, відхиляє домен, `ECR-TMPL-0409`).
 */
export function ColumnEditor({
  draft,
  disabled,
  saving,
  onChange,
  onSubmit,
  onCancel,
}: {
  draft: ColumnDraft;
  disabled: boolean;
  saving: boolean;
  onChange: (next: ColumnDraft) => void;
  onSubmit: () => void;
  onCancel: () => void;
}): JSX.Element {
  const blocker = whyCannotSaveColumn(draft);
  const isDecimal = draft.dataType === 'Decimal';
  const isLookup = draft.dataType === 'Lookup';

  // ⛔ Директива registry-lookup, PR A3: колонку `Lookup` конфігурували
  // сирим числовим `RegistryDefId` — автор шаблону мав знати ідентифікатор
  // напам'ять, узятий десь поза цим екраном. Запит лінивий (лише коли
  // форма відкрита на колонці Lookup), бо саме тоді список і потрібен.
  const registries = useQuery({
    queryKey: queryKeys.registries.list(),
    queryFn: () => apiFetch<RegistryDefDto[]>('/api/v1/registries'),
    enabled: isLookup,
  });

  // ⚠ Мемоізовано: `[]`-літерал у пропі `data` перебудовувався б щорендеру,
  // поки запит іще `pending`, — новий референс масиву на кожен рендер.
  const registryOptions = useMemo(
    () =>
      (registries.data ?? []).map((registry) => ({
        value: String(registry.id),
        label: `${localized(registry.nameL10n)} (${registry.code})`,
      })),
    [registries.data],
  );

  return (
    <Stack gap="sm">
      <TextInput
        label={t('columns.code')}
        description={t('columns.codeHint')}
        value={draft.code}
        disabled={disabled || !draft.isNew}
        onChange={(event) => onChange({ ...draft, code: event.currentTarget.value })}
      />

      <LocalizedInput
        label={t('columns.header')}
        description={t('columns.headerHint')}
        value={draft.headerL10n}
        onChange={(headerL10n) => onChange({ ...draft, headerL10n })}
      />

      <Select
        label={t('columns.dataType')}
        description={t('columns.dataTypeHint')}
        data={EditableDataTypes}
        value={draft.dataType}
        disabled={disabled || !draft.isNew}
        allowDeselect={false}
        onChange={(value) => {
          if (value !== null) onChange({ ...draft, dataType: value as ColumnDraft['dataType'] });
        }}
      />

      <NumberInput
        label={t('columns.ordinal')}
        description={t('columns.ordinalHint')}
        disabled={disabled}
        value={draft.ordinal ?? ''}
        onChange={(value) =>
          onChange({ ...draft, ordinal: typeof value === 'number' ? value : draft.ordinal })
        }
      />

      <Switch
        label={t('columns.required')}
        disabled={disabled}
        checked={draft.isRequired}
        onChange={(event) => onChange({ ...draft, isRequired: event.currentTarget.checked })}
      />

      <Switch
        label={t('columns.readOnly')}
        disabled={disabled}
        checked={draft.isReadOnly}
        onChange={(event) => onChange({ ...draft, isReadOnly: event.currentTarget.checked })}
      />

      <Switch
        label={t('columns.hidden')}
        description={t('columns.hiddenHint')}
        disabled={disabled}
        checked={draft.isHidden}
        onChange={(event) => onChange({ ...draft, isHidden: event.currentTarget.checked })}
      />

      <TextInput
        label={t('columns.displayFormat')}
        description={t('columns.displayFormatHint')}
        disabled={disabled}
        value={draft.displayFormat}
        onChange={(event) => onChange({ ...draft, displayFormat: event.currentTarget.value })}
      />

      <TextInput
        label={t('columns.defaultValue')}
        description={t('columns.defaultValueHint')}
        disabled={disabled}
        value={draft.defaultValue}
        onChange={(event) => onChange({ ...draft, defaultValue: event.currentTarget.value })}
      />

      {isDecimal && (
        <Group grow>
          <NumberInput
            label={t('columns.precision')}
            disabled={disabled}
            min={1}
            value={draft.precision ?? ''}
            onChange={(value) =>
              onChange({ ...draft, precision: typeof value === 'number' ? value : null })
            }
          />
          <NumberInput
            label={t('columns.scale')}
            disabled={disabled}
            min={0}
            value={draft.scale ?? ''}
            onChange={(value) => onChange({ ...draft, scale: typeof value === 'number' ? value : null })}
          />
        </Group>
      )}

      {isLookup && (
        <Select
          label={t('columns.lookupRegistryDefId')}
          description={t('columns.lookupRegistryDefIdHint')}
          disabled={disabled}
          searchable
          nothingFoundMessage={t('columns.lookupRegistryDefIdEmpty')}
          data={registryOptions}
          // ⚠ Рядок, не число: Mantine `Select` завжди працює з текстовим
          // `value`. Наявні колонки з уже заданим (сирим) ідентифікатором
          // і далі показують правильно вибраний довідник — досить, щоб він
          // був серед завантажених `data` (зворотна сумісність, не міграція
          // даних).
          value={draft.lookupRegistryDefId === null ? null : String(draft.lookupRegistryDefId)}
          onChange={(value) =>
            onChange({ ...draft, lookupRegistryDefId: value === null ? null : Number(value) })
          }
        />
      )}

      {!draft.hasFullData && (
        <Alert color="statusWarning">{t('columns.partialDataWarning')}</Alert>
      )}

      {blocker !== null && <Alert color="statusWarning">{blockerLabel(blocker)}</Alert>}

      <Group>
        <Button variant="default" onClick={onCancel}>
          {t('common.cancel')}
        </Button>
        <Button disabled={disabled || blocker !== null} loading={saving} onClick={onSubmit}>
          {t('columns.save')}
        </Button>
      </Group>
    </Stack>
  );
}

/** Підпис причини, з якої зберегти ще не можна. */
function blockerLabel(blocker: ColumnBlocker): string {
  switch (blocker) {
    case 'Code':
      return t('columns.errCode');
    case 'Header':
      return t('columns.errHeader');
    case 'Scale':
      return t('columns.errScale');
    default:
      return blocker;
  }
}
