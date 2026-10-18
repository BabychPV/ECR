import type { JSX } from 'react';
import { Button, Checkbox, Group, NativeSelect, Paper, Stack, Text, TextInput } from '@mantine/core';
import type { RegistryDefinitionDto } from '@/api/types';
import { t } from '@/shared/i18n';
import {
  EditableFieldDataTypes,
  NumericFieldTypes,
  type FieldDataType,
  type FieldDraft,
} from './definition';
import { CompositionFieldOptions } from './rc816/CompositionFieldOptions';

/** Що означає тип значення — літералами, щоб сторож каталогу бачив кожен ключ. */
function typeHint(dataType: FieldDataType): string {
  switch (dataType) {
    case 'String':
      return t('registries.fieldForm.type.String');
    case 'Int':
      return t('registries.fieldForm.type.Int');
    case 'Decimal':
      return t('registries.fieldForm.type.Decimal');
    case 'Bool':
      return t('registries.fieldForm.type.Bool');
    case 'Date':
      return t('registries.fieldForm.type.Date');
    case 'Lookup':
      return t('registries.fieldForm.type.Lookup');
    case 'Unit':
      return t('registries.fieldForm.type.Unit');
  }
}

/**
 * Форма НОВОГО поля довідника (`ФВ-8.3`, `ФВ-8.12`): кожна властивість — окремий підписаний
 * контрол із підказкою «що це», а не клітинка таблиці.
 *
 * ⚠ Лише те, що є в `RegistryFieldSaveDto`: код, назва, тип, обов'язковість, ключ, ціль
 * посилання, одиниця, композиція. Розміру рядка, точності й значення за замовчуванням у моделі
 * немає — їх тут немає й у формі.
 *
 * ⚠ «Обов'язкове» активне лише коли в довіднику немає живих записів (`mayBeRequired`): інакше
 * сервер відхилить збереження (`newFieldsMayBeRequired`), а форма пояснює це до натискання.
 * Стан з чернетки (`isRequired` уже стоїть) лишається вимкнуваним, щоб його можна було зняти.
 */
export function RegistryFieldForm({
  definition,
  draft,
  index,
  otherNew,
  canEdit,
  mayBeRequired,
  registryOptions,
  unitOptions,
  onChange,
  onRemove,
}: {
  readonly definition: RegistryDefinitionDto;
  readonly draft: FieldDraft;
  readonly index: number;
  readonly otherNew: readonly FieldDraft[];
  readonly canEdit: boolean;
  readonly mayBeRequired: boolean;
  readonly registryOptions: readonly { value: string; label: string }[];
  readonly unitOptions: readonly { value: string; label: string }[];
  readonly onChange: (field: FieldDraft) => void;
  readonly onRemove: () => void;
}): JSX.Element {
  const isLookup = draft.dataType === 'Lookup';
  const isNumeric = NumericFieldTypes.includes(draft.dataType);
  const requiredLocked = !mayBeRequired;

  return (
    <Paper withBorder p="sm" radius="sm" data-focus-row="" data-registry-field-form="">
      <Stack gap="xs">
        <Group justify="space-between" align="center">
          <Text fw={600} size="sm">
            {t('registries.fieldForm.title')}
          </Text>
          <Button
            size="xs"
            variant="subtle"
            color="statusError"
            disabled={!canEdit}
            // ⚠ Кнопки «Прибрати» однакові в кожній формі — ім'я несе код поля.
            aria-label={`${t('registries.removeField')}: ${draft.code || String(index + 1)}`}
            onClick={onRemove}
          >
            {t('registries.removeField')}
          </Button>
        </Group>

        <TextInput
          label={t('registries.code')}
          description={t('registries.fieldForm.codeHint')}
          disabled={!canEdit}
          value={draft.code}
          error={canEdit && draft.code.trim() === '' ? t('registries.fieldForm.errCode') : undefined}
          onChange={(event) => onChange({ ...draft, code: event.currentTarget.value })}
        />

        <TextInput
          label={t('registries.name')}
          description={t('registries.fieldForm.nameHint')}
          disabled={!canEdit}
          value={draft.name}
          error={canEdit && draft.name.trim() === '' ? t('registries.fieldForm.errName') : undefined}
          onChange={(event) => onChange({ ...draft, name: event.currentTarget.value })}
        />

        <NativeSelect
          label={t('registries.dataType')}
          description={typeHint(draft.dataType)}
          disabled={!canEdit}
          value={draft.dataType}
          data={[...EditableFieldDataTypes]}
          onChange={(event) => {
            const dataType = event.currentTarget.value as FieldDataType;
            onChange({
              ...draft,
              dataType,
              lookupRegistryDefId: dataType === 'Lookup' ? draft.lookupRegistryDefId : null,
              unitId: NumericFieldTypes.includes(dataType) ? draft.unitId : null,
            });
          }}
        />

        {isLookup && (
          <>
            <NativeSelect
              label={t('registries.lookup')}
              description={t('registries.fieldForm.lookupHint')}
              disabled={!canEdit}
              value={draft.lookupRegistryDefId === null ? '' : String(draft.lookupRegistryDefId)}
              error={
                canEdit && draft.lookupRegistryDefId === null
                  ? t('registries.fieldForm.errLookup')
                  : undefined
              }
              data={[{ value: '', label: '—' }, ...registryOptions]}
              onChange={(event) =>
                onChange({
                  ...draft,
                  lookupRegistryDefId:
                    event.currentTarget.value === '' ? null : Number(event.currentTarget.value),
                })
              }
            />
            <CompositionFieldOptions
              definition={definition}
              draft={draft}
              otherNew={otherNew}
              canEdit={canEdit}
              onChange={onChange}
            />
          </>
        )}

        {isNumeric && (
          <NativeSelect
            label={t('registries.unit')}
            description={t('registries.fieldForm.unitHint')}
            disabled={!canEdit}
            value={draft.unitId === null ? '' : String(draft.unitId)}
            data={[{ value: '', label: '—' }, ...unitOptions]}
            onChange={(event) =>
              onChange({
                ...draft,
                unitId: event.currentTarget.value === '' ? null : Number(event.currentTarget.value),
              })
            }
          />
        )}

        <Checkbox
          label={t('registries.required')}
          description={
            requiredLocked && !draft.isRequired
              ? t('registries.fieldForm.requiredLocked')
              : t('registries.fieldForm.requiredHint')
          }
          error={requiredLocked && draft.isRequired ? t('registries.fieldForm.requiredLocked') : undefined}
          // ⚠ Не вимикаємо вже відмічений прапорець: стан із чернетки треба вміти зняти.
          disabled={!canEdit || (requiredLocked && !draft.isRequired)}
          checked={draft.isRequired}
          onChange={(event) => onChange({ ...draft, isRequired: event.currentTarget.checked })}
        />

        <Checkbox
          label={t('registries.keyField')}
          description={t('registries.fieldForm.keyHint')}
          disabled={!canEdit}
          checked={draft.isKey}
          onChange={(event) => onChange({ ...draft, isKey: event.currentTarget.checked })}
        />
      </Stack>
    </Paper>
  );
}
