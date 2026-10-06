import { useDeferredValue, type JSX } from 'react';
import { Group, Text } from '@mantine/core';
import type { TableSliceDto } from '@/api/types';
import { formatDecimal } from '@/shared/format/number';
import { t } from '@/shared/i18n';
import type { TotalsEdit } from './gridTotals';
import { useSelectedRange } from './selectionStore';
import { selectionStats } from './selectionStats';

interface GridStatusBarProps {
  readonly tableInstanceId: number;
  readonly periodKey: number;
  readonly slice: TableSliceDto;
  /** Колонки, які отримала сітка (з колонкою підпису, якщо вона є). */
  readonly columns: readonly { readonly prop?: string | number }[];
  /** Моделі рядків, які отримала сітка. */
  readonly rows: readonly Record<string, unknown>[];
  /** Незбережені правки зрізу, ключ — `rowKey:columnCode`. */
  readonly pending?: ReadonlyMap<string, TotalsEdit>;
}

/**
 * Рядок стану під сіткою (UI-23; макет `screen-document.js`, `.statusbar` /
 * `selstats`): для діапазону з двох і більше чисел — «Average · Count · Sum»,
 * інакше — розмір таблиці.
 *
 * ⚠ Підрахунок відкладено (`useDeferredValue`): рух виділення не чекає на
 * суму великого діапазону, а сама сітка не перемальовується — виділення
 * приходить зі сховища (`selectionStore.ts`), не зі стану `DocumentGrid`.
 *
 * ⚠ Без `aria-live`: читалка не має озвучувати кожен рух курсора (ризик із
 * `UI-ADOPTION-TASKS`, UI-23); рядок читається на вимогу.
 */
export function GridStatusBar(props: GridStatusBarProps): JSX.Element {
  const range = useDeferredValue(useSelectedRange(props.tableInstanceId, props.periodKey));

  const stats = selectionStats({
    slice: props.slice,
    columns: props.columns,
    rows: props.rows,
    range,
    ...(props.pending === undefined ? {} : { pending: props.pending }),
  });

  return (
    <Group
      gap="md"
      justify="flex-end"
      wrap="nowrap"
      px="xs"
      data-testid="grid-status-bar"
    >
      {stats === null ? (
        <Text size="xs" c="dimmed" data-testid="grid-status-size">
          {t('grid.status.size', { rows: props.slice.rows.length, columns: props.slice.columns.length })}
        </Text>
      ) : (
        <>
          <Text size="xs" c="dimmed" data-testid="grid-status-average">
            {t('grid.status.average')} <b>{formatDecimal(stats.average) ?? stats.average}</b>
          </Text>
          <Text size="xs" c="dimmed" data-testid="grid-status-count">
            {t('grid.status.count')} <b>{stats.count}</b>
          </Text>
          <Text size="xs" c="dimmed" data-testid="grid-status-sum">
            {t('grid.status.sum')} <b>{formatDecimal(stats.sum) ?? stats.sum}</b>
          </Text>
        </>
      )}
    </Group>
  );
}
