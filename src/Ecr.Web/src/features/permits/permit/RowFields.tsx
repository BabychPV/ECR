import type { JSX } from 'react';
import { Button, Select, Switch, Text, TextInput } from '@mantine/core';
import { t } from '@/shared/i18n';
import { scopeOf, type PickerScope } from '../lib/pickerScope';
import { DateField } from './DateField';
import { isLookup, pickerOptions, type FormValues, type PermitField, type PickerData } from './model';
import './permit.css';

export interface RowFieldsProps {
  readonly fields: readonly PermitField[];
  readonly form: FormValues;
  /** Помилки полів за кодом стовпця (текст уже готовий). */
  readonly errors: Readonly<Record<string, string>>;
  readonly onChange: (code: string, value: string) => void;
  readonly readOnly: boolean;
  readonly picker: PickerData;
  /** Значення пікерів ще читаються. */
  readonly pickerPending: boolean;
  /**
   * «Not in the list? Add a value…»: для поля — дія, що відкриває діалог додавання, або `null`/`undefined`, якщо додати
   * нічого (нема права, список — таблиця дозволу, закінчений дозвіл).
   */
  readonly addValue?: (field: PermitField) => (() => void) | null;
}

function scopeHint(field: PermitField, scope: PickerScope): string {
  const source = field.source;
  switch (scope) {
    case 'shared':
      return t('permits.page.row.hintShared', { name: source.listRegistryCode ?? '' });
    case 'type':
      return t('permits.page.row.hintType');
    case 'permit':
      return t('permits.page.row.hintPermit', { name: source.siblingTableCode ?? '' });
  }
}

function Field({ field, form, errors, onChange, readOnly, picker, pickerPending, addValue }: RowFieldsProps & { field: PermitField }): JSX.Element {
  const { column } = field;
  const value = form[column.code] ?? '';
  const error = errors[column.code];
  const required = column.required === true;

  if (column.type === 'bool') {
    return (
      <Switch
        label={column.label}
        checked={value === 'true'}
        disabled={readOnly}
        onChange={(event) => onChange(column.code, event.currentTarget.checked ? 'true' : 'false')}
      />
    );
  }

  if (isLookup(column)) {
    const options = pickerOptions(field, picker);
    const add = readOnly ? null : (addValue?.(field) ?? null);
    const select = (
      <Select
        label={column.label}
        required={required}
        data={options}
        value={value === '' ? null : value}
        onChange={(next) => onChange(column.code, next ?? '')}
        disabled={readOnly}
        searchable
        clearable={!required}
        error={error}
        description={scopeHint(field, scopeOf(column))}
        placeholder={pickerPending ? t('common.loading') : undefined}
        nothingFoundMessage={t('permits.page.row.noValues')}
      />
    );
    if (add === null) return select;

    return (
      <div className="pp-with-add" data-add-value-field={column.code}>
        {select}
        <Button variant="subtle" size="xs" onClick={add} data-add-value="">
          {t('permits.page.row.addValue')}
        </Button>
      </div>
    );
  }

  if (column.type === 'date') {
    return (
      <DateField
        label={column.label}
        required={required}
        value={value}
        error={error}
        disabled={readOnly}
        onChange={(next) => onChange(column.code, next)}
      />
    );
  }

  return (
    <TextInput
      label={column.label}
      required={required}
      value={value}
      readOnly={readOnly}
      error={error}
      onChange={(event) => onChange(column.code, event.currentTarget.value)}
      {...(column.type === 'number' ? { inputMode: 'decimal' as const, styles: { input: { fontFamily: 'var(--mantine-font-family-monospace)' } } } : {})}
      {...(column.unit === undefined
        ? {}
        : {
            rightSection: (
              <Text size="xs" c="dimmed">
                {column.unit}
              </Text>
            ),
            rightSectionWidth: 56,
          })}
    />
  );
}

/**
 * Поля форми рядка за типами стовпців (мокап `field(c)`): текст, число з одиницею, дата, Yes/No, списки з трьох
 * областей. Групи стовпців — розгортані блоки; перша група відкрита, решта — лише коли в ній є обов'язкове поле
 * нового рядка чи помилка.
 *
 * «Not in the list? Add a value…» (PM-08e): під полем спільного списку чи списку лише цього типу, якщо `addValue`
 * дає дію (є право на список); діалог пише значення в сам список, а не в дозвіл.
 */
export function RowFields(props: RowFieldsProps & { readonly isNew: boolean }): JSX.Element {
  const groups = new Map<string, PermitField[]>();
  for (const field of props.fields) {
    const name = field.column.group ?? t('permits.page.row.groupMain');
    groups.set(name, [...(groups.get(name) ?? []), field]);
  }

  return (
    <div className="pp-stack">
      {[...groups.entries()].map(([name, members], index) => {
        const open =
          index === 0 ||
          members.some((member) => props.errors[member.column.code] !== undefined) ||
          (props.isNew && members.some((member) => member.column.required === true));
        const content = (
          <div className="pp-form-grid">
            {members.map((field) => (
              <Field key={field.column.code} {...props} field={field} />
            ))}
          </div>
        );

        return index === 0 && groups.size === 1 ? (
          <div key={name}>{content}</div>
        ) : (
          <details key={name} open={open} className="pp-group">
            <summary>{`${name} (${t('permits.page.row.fieldCount', { count: members.length })})`}</summary>
            {content}
          </details>
        );
      })}
    </div>
  );
}
