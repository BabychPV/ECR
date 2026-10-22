import type { JSX } from 'react';
import { Box, CheckIcon, Group, Select, Text, type ComboboxItem, type ComboboxParsedItem, type OptionsFilter } from '@mantine/core';
import type { LookupTargetData, LookupTargetOption } from './lookupTargets';

/**
 * Пошук цілі по назві І по опису (KIT §6.8.1): регістр не важить, група без збігів зникає з розкритого списку.
 * Без пошукового рядка - усе як є.
 */
export const lookupTargetFilter: OptionsFilter = ({ options, search }) => {
  const needle = search.trim().toLowerCase();
  if (needle === '') return options;

  const matches = (item: ComboboxItem): boolean =>
    item.label.toLowerCase().includes(needle) ||
    ((item as Partial<LookupTargetOption>).description ?? '').toLowerCase().includes(needle);
  const result: ComboboxParsedItem[] = [];

  for (const option of options) {
    if ('group' in option) {
      const items = option.items.filter(matches);
      if (items.length > 0) result.push({ ...option, items });
    } else if (matches(option)) {
      result.push(option);
    }
  }

  return result;
};

export interface LookupTargetSelectProps {
  readonly label: string;
  readonly description?: string | undefined;
  readonly disabled?: boolean;
  readonly nothingFoundMessage: string;
  readonly data: LookupTargetData;
  /** Ідентифікатор обраної цілі; `null` - не обрано. */
  readonly value: number | null;
  readonly onChange: (next: number | null) => void;
}

/**
 * Вибір цілі Lookup для шапки шаблону (`HeaderFieldEditor`) і колонки (`ColumnEditor`): ОДИН компонент на обидва місця
 * (KIT §6.8.1). Групи «Permits» (шапки типів і групи типів) та «Dictionaries» приходять готовими з
 * `lookupTargetSelectData`; опція групи типів дворядкова - назва і приглушений опис (один рядок, повний текст у `title`).
 *
 * ⚠ Обране значення в полі - одним рядком (`label`); опис лише в розкритому списку. Опис входить у доступне ім'я опції
 * (вміст опції), тож його чує і читалка; `aria-describedby` на самій опції Mantine через `renderOption` поставити не дає.
 */
export function LookupTargetSelect({
  label,
  description,
  disabled = false,
  nothingFoundMessage,
  data,
  value,
  onChange,
}: LookupTargetSelectProps): JSX.Element {
  return (
    <Select
      label={label}
      description={description}
      disabled={disabled}
      searchable
      nothingFoundMessage={nothingFoundMessage}
      data={data}
      filter={lookupTargetFilter}
      // ⚠ Рядок, не число: Mantine `Select` завжди працює з текстовим `value`.
      value={value === null ? null : String(value)}
      onChange={(next) => onChange(next === null ? null : Number(next))}
      renderOption={({ option, checked }) => {
        const second = (option as Partial<LookupTargetOption>).description;

        return (
          <Group gap="xs" wrap="nowrap" style={{ width: '100%' }}>
            <Box style={{ flex: 1, minWidth: 0 }}>
              <Text size="sm">{option.label}</Text>
              {second !== undefined && (
                <Text size="xs" c="dimmed" truncate="end" title={second} data-lookup-target-description="">
                  {second}
                </Text>
              )}
            </Box>
            {checked && <CheckIcon size={12} />}
          </Group>
        );
      }}
    />
  );
}
