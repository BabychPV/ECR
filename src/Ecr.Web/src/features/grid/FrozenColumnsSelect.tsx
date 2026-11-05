import { useId, type JSX } from 'react';
import { Group, NativeSelect, Text } from '@mantine/core';
import { t } from '@/shared/i18n';
import {
  FrozenDefaultChoice,
  FrozenNoneChoice,
  frozenChoices,
  frozenChoiceValue,
  frozenOverrideOfChoice,
} from './frozenColumns';

/**
 * Вибір «Freeze columns» у панелі сітки: Default / None / «Up to <колонка>» для перших колонок.
 *
 * ⚠ Нативний `<select>`: керується клавіатурою зі скриптридером «з коробки», а підпис - справжній
 * `<label for>`, не лише `aria-label`. Значення - кількість закріплених колонок разом з колонкою
 * підпису рядка (`frozenColumns.ts`).
 */
export function FrozenColumnsSelect({
  columnNames,
  override,
  onChange,
}: {
  /** Заголовки колонок СІТКИ в порядку відображення (колонка підпису - перша, якщо є). */
  readonly columnNames: readonly string[];
  /** Вибір користувача; `undefined` - типове правило. */
  readonly override: number | undefined;
  readonly onChange: (override: number | undefined) => void;
}): JSX.Element | null {
  const id = useId();
  const choices = frozenChoices(columnNames);
  const current = frozenChoiceValue(override, columnNames.length);

  // Збережене значення поза переліком (колонок стало менше/більше) все одно показується чесно.
  if (!choices.some((choice) => choice.value === current)) {
    choices.push({ value: current, columnName: columnNames[Number(current) - 1] ?? '' });
  }

  if (columnNames.length < 2) return null;

  const data = choices.map((choice) => ({
    value: choice.value,
    label:
      choice.value === FrozenDefaultChoice
        ? t('grid.frozenColumns.default')
        : choice.value === FrozenNoneChoice
          ? t('grid.frozenColumns.none')
          : t('grid.frozenColumns.upTo', { column: choice.columnName ?? '' }),
  }));

  return (
    <Group gap="xs" wrap="nowrap" data-frozen-columns="">
      <Text component="label" htmlFor={id} size="sm">
        {t('grid.frozenColumns.label')}
      </Text>
      <NativeSelect
        id={id}
        size="xs"
        data={data}
        value={current}
        onChange={(event) => onChange(frozenOverrideOfChoice(event.currentTarget.value))}
      />
    </Group>
  );
}
