import type { JSX } from 'react';
import { ActionIcon, Checkbox, Grid, Group, Select, TextInput } from '@mantine/core';
import { t } from '@/shared/i18n';
import type { UnitOption } from './names';
import { SimpleColumnKinds, isNumericKind, type DraftColumn } from './ops';

/**
 * Поля одного стовпця: назва, тип, одиниця, «обов'язковий», «ключ» (Permits, PM-08c).
 *
 * Спільні для діалогу «Add a column» і кроку «Columns» майстра «Add a table»: у мокапі це одна й та
 * сама форма. Компонент керований — стан тримає викликач.
 *
 * ⚠ Тип `Lookup` (список) тут не створюється: його налаштовує інспектор стовпця. Але стовпець,
 * скопійований з іншої таблиці, може його мати, тоді тип показується й заблокований.
 * ⚠ Одиниця доступна лише числовим типам (`Decimal`, `Int`); зміна типу на нечисловий її скидає.
 */
export interface ColumnFieldsProps {
  /** Префікс `id` полів: за ним крок майстра ставить фокус на хибне поле. */
  readonly idPrefix: string;

  /** Підпис поля назви: «Name (English)» у діалозі, «Column 2» у майстрі. */
  readonly nameLabel: string;
  readonly value: DraftColumn;
  readonly units: readonly UnitOption[];

  /** Показувати «Key» (у діалозі одного стовпця ключ не міняється). */
  readonly showKey: boolean;
  readonly nameError?: string | undefined;

  /** Поле назви — початковий фокус діалогу (`data-autofocus` Mantine). */
  readonly autoFocusName?: boolean | undefined;
  readonly onChange: (patch: Partial<DraftColumn>) => void;

  /** Є лише у майстрі: прибрати стовпець. */
  readonly onRemove?: (() => void) | undefined;
  readonly removeDisabled?: boolean | undefined;
}

export function ColumnFields({
  idPrefix,
  nameLabel,
  value,
  units,
  showKey,
  nameError,
  autoFocusName,
  onChange,
  onRemove,
  removeDisabled,
}: ColumnFieldsProps): JSX.Element {
  const typeData: { value: string; label: string }[] = SimpleColumnKinds.map((kind) => ({
    value: kind,
    label: t(`permits.editor.dialogs.type.${kind}`),
  }));
  if (!SimpleColumnKinds.some((kind) => kind === value.dataType)) {
    typeData.push({
      value: value.dataType,
      label: t(`permits.editor.dialogs.type.${value.dataType}`),
    });
  }

  const numeric = isNumericKind(value.dataType);

  return (
    <Grid gutter="xs" align="flex-end">
      <Grid.Col span={{ base: 12, sm: 5 }}>
        <TextInput
          id={`${idPrefix}-name`}
          label={nameLabel}
          value={value.name}
          error={nameError}
          {...(autoFocusName === true ? { 'data-autofocus': true } : {})}
          onChange={(event) => {
            onChange({ name: event.currentTarget.value });
          }}
        />
      </Grid.Col>

      <Grid.Col span={{ base: 12, sm: 4 }}>
        <Select
          id={`${idPrefix}-type`}
          label={t('permits.editor.dialogs.col.type')}
          data={typeData}
          value={value.dataType}
          allowDeselect={false}
          disabled={value.dataType === 'Lookup'}
          onChange={(next) => {
            if (next === null) return;
            onChange(isNumericKind(next) ? { dataType: next } : { dataType: next, unitId: null });
          }}
        />
      </Grid.Col>

      <Grid.Col span={{ base: 12, sm: 3 }}>
        <Select
          id={`${idPrefix}-unit`}
          label={t('permits.editor.dialogs.col.unit')}
          data={units.map((unit) => ({ value: String(unit.id), label: unit.label }))}
          value={value.unitId === null ? null : String(value.unitId)}
          placeholder={t('permits.editor.dialogs.col.noUnit')}
          searchable
          clearable
          disabled={!numeric}
          onChange={(next) => {
            onChange({ unitId: next === null ? null : Number(next) });
          }}
        />
      </Grid.Col>

      <Grid.Col span={12}>
        <Group gap="md" wrap="wrap">
          <Checkbox
            id={`${idPrefix}-required`}
            label={t('permits.editor.dialogs.col.required')}
            checked={value.required || value.key}
            disabled={value.key}
            onChange={(event) => {
              onChange({ required: event.currentTarget.checked });
            }}
          />

          {showKey ? (
            <Checkbox
              id={`${idPrefix}-key`}
              label={t('permits.editor.dialogs.col.key')}
              checked={value.key}
              onChange={(event) => {
                onChange({ key: event.currentTarget.checked });
              }}
            />
          ) : null}

          {onRemove === undefined ? null : (
            <ActionIcon
              variant="subtle"
              color="statusError"
              aria-label={t('permits.editor.dialogs.col.remove')}
              disabled={removeDisabled === true}
              onClick={onRemove}
            >
              ×
            </ActionIcon>
          )}
        </Group>
      </Grid.Col>
    </Grid>
  );
}
