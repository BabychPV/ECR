import { useMemo, type JSX } from 'react';
import { Alert, Button, Group, NumberInput, Select, Skeleton, Stack, Switch, TextInput } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import { queryKeys } from '@/api/queryKeys';
import type { RegistryDefDto } from '@/api/types';
import { t } from '@/shared/i18n';
import { localized } from '@/shared/i18n/localized';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { LocalizedInput } from '@/shared/ui/LocalizedInput';
import { EditableDataTypes } from './column';
import {
  type HeaderFieldBlocker,
  type HeaderFieldDraft,
  whyCannotSaveHeaderField,
} from './headerField';

/**
 * Форма поля шапки документа версії шаблону — за зразком `ColumnEditor.tsx`
 * (`W5.2`), той самий вибір довідника для типу `Lookup` (`GET
 * /api/v1/registries`, лінивий запит лише коли форма відкрита на полі
 * Lookup).
 *
 * ⚠ Без стилю, точності/масштабу, формату показу, значення за замовчуванням
 * і одиниці виміру — жодного з них шапка документа не носить
 * (`HeaderFieldDef.cs`): у колонки вони існують заради презентаційного шару
 * й числових типів таблиці, а поле шапки — це один запис на документ, не
 * клітинка.
 *
 * ⚠ Форма нічого не вирішує про стан версії — так само, як `ColumnEditor`:
 * `disabled` лише заважає заповнювати те, що однаково не збережеться
 * (`isEditable` рахує сервер, відхиляє домен, `ECR-TMPL-0409`).
 */
export function HeaderFieldEditor({
  draft,
  disabled,
  saving,
  onChange,
  onSubmit,
  onCancel,
}: {
  draft: HeaderFieldDraft;
  disabled: boolean;
  saving: boolean;
  onChange: (next: HeaderFieldDraft) => void;
  onSubmit: () => void;
  onCancel: () => void;
}): JSX.Element {
  const blocker = whyCannotSaveHeaderField(draft);
  const isLookup = draft.dataType === 'Lookup';

  const registries = useQuery({
    queryKey: queryKeys.registries.list(),
    queryFn: () => apiFetch<RegistryDefDto[]>('/api/v1/registries'),
    enabled: isLookup,
  });

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
        label={t('headerFields.code')}
        description={t('headerFields.codeHint')}
        value={draft.code}
        disabled={disabled || !draft.isNew}
        onChange={(event) => onChange({ ...draft, code: event.currentTarget.value })}
      />

      <LocalizedInput
        label={t('headerFields.label')}
        description={t('headerFields.labelHint')}
        value={draft.labelL10n}
        onChange={(labelL10n) => onChange({ ...draft, labelL10n })}
      />

      <Select
        label={t('headerFields.dataType')}
        description={t('headerFields.dataTypeHint')}
        data={EditableDataTypes}
        value={draft.dataType}
        disabled={disabled || !draft.isNew}
        allowDeselect={false}
        onChange={(value) => {
          if (value !== null) onChange({ ...draft, dataType: value as HeaderFieldDraft['dataType'] });
        }}
      />

      <NumberInput
        label={t('headerFields.ordinal')}
        description={t('headerFields.ordinalHint')}
        disabled={disabled}
        value={draft.ordinal ?? ''}
        onChange={(value) =>
          onChange({ ...draft, ordinal: typeof value === 'number' ? value : null })
        }
      />

      <Switch
        label={t('headerFields.required')}
        disabled={disabled}
        checked={draft.isRequired}
        onChange={(event) => onChange({ ...draft, isRequired: event.currentTarget.checked })}
      />

      {/*
        ⛔ Директива D15 §0, правило L10 (та сама, що в `ColumnEditor.tsx`):
        відмова `GET /api/v1/registries` не повинна виглядати як «довідників
        не завели» — порожній `Select` тут не малюється взагалі, коли даних
        немає.
      */}
      {isLookup && registries.error !== null && (
        <ErrorAlert error={registries.error} onRetry={() => void registries.refetch()} />
      )}

      {isLookup && registries.error === null && registries.isPending && (
        <Skeleton height={60} radius="sm" data-registry-select="pending" />
      )}

      {/*
        ⚠ Поле вибору довідника показується ЛИШЕ для типу Lookup — той самий
        мутаційний доказ, що й у `headerFieldBody` (`headerField.ts`): якщо
        цю умову прибрати, поле з'явиться для решти типів, для яких сервер
        `SetLookup` однаково відхилив би.
      */}
      {isLookup && registries.error === null && !registries.isPending && (
        <Select
          label={t('headerFields.lookupRegistryDefId')}
          description={t('headerFields.lookupRegistryDefIdHint')}
          disabled={disabled}
          searchable
          nothingFoundMessage={t('headerFields.lookupRegistryDefIdEmpty')}
          data={registryOptions}
          value={draft.lookupRegistryDefId === null ? null : String(draft.lookupRegistryDefId)}
          onChange={(value) =>
            onChange({ ...draft, lookupRegistryDefId: value === null ? null : Number(value) })
          }
        />
      )}

      {blocker !== null && <Alert color="statusWarning">{blockerLabel(blocker)}</Alert>}

      <Group>
        <Button variant="default" onClick={onCancel}>
          {t('common.cancel')}
        </Button>
        <Button disabled={disabled || blocker !== null} loading={saving} onClick={onSubmit}>
          {t('headerFields.save')}
        </Button>
      </Group>
    </Stack>
  );
}

/** Підпис причини, з якої зберегти ще не можна. */
function blockerLabel(blocker: HeaderFieldBlocker): string {
  switch (blocker) {
    case 'CodeEmpty':
      return t('headerFields.errCode');
    case 'CodeInvalid':
      return t('headerFields.errCodeInvalid');
    case 'Label':
      return t('headerFields.errLabel');
    case 'LookupRequired':
      return t('headerFields.errLookupRequired');
    default:
      return blocker;
  }
}
