import type { JSX } from 'react';
import { Badge, Group, Text } from '@mantine/core';
import { t } from '@/shared/i18n';
import { formatDecimal, sumDecimals, withinTolerance, type ChildSumRule } from './composition';
import type { PendingRow } from './pendingRows';

/** Стан живого індикатора Σ для одного правила. */
export interface SumState {
  /** Σ інваріантним записом. */
  readonly sum: string;
  /** Рядків, що беруть участь (не видалених). */
  readonly rows: number;
  /** Значень, які не вдалося прочитати числом, — вони не в Σ. */
  readonly skipped: number;
  /** `true` — у допуску; `false` — ні; `null` — складу ще немає (правило не діє) або ціль нечитабельна. */
  readonly ok: boolean | null;
}

/**
 * Σ поля дочірніх рядків — з поточних значень панелі, разом із незбереженими.
 *
 * ⚠ Лише для показу (§8.4): авторитетний результат — правило на сервері. Порожній склад —
 * `ok: null`, а не порушення: згенерований вираз має хвіст `OR REGCOUNT(…) = 0` (RT-17a).
 */
export function sumState(rule: ChildSumRule, rows: readonly PendingRow[]): SumState {
  const live = rows.filter((row) => !row.deleted);
  const { sum, skipped } = sumDecimals(live.map((row) => row.values[rule.field]));

  return {
    sum: formatDecimal(sum),
    rows: live.length,
    skipped,
    ok: live.length === 0 ? null : withinTolerance(sum, rule.target, rule.tolerance),
  };
}

/** Рядок «Σ поле = … · ціль … ± …» під панеллю частин. */
export function SumIndicator({
  rule,
  rows,
}: {
  readonly rule: ChildSumRule;
  readonly rows: readonly PendingRow[];
}): JSX.Element {
  const state = sumState(rule, rows);

  return (
    <Group gap="xs" data-rc816-sum={rule.code} role="status">
      <Text size="sm" fw={600}>
        {t('registries.rc816.sum', { field: rule.field, sum: state.sum })}
      </Text>
      <Text size="sm" c="dimmed">
        {t('registries.rc816.sumTarget', { target: rule.target, tolerance: rule.tolerance })}
      </Text>
      {state.ok === true && (
        <Badge color="statusSuccess" variant="light">
          {t('registries.rc816.sumOk')}
        </Badge>
      )}
      {state.ok === false && (
        <Badge color={rule.severity === 'Error' ? 'statusError' : 'statusWarning'} variant="light">
          {t('registries.rc816.sumOff')}
        </Badge>
      )}
      {state.ok === null && state.rows === 0 && (
        <Text size="xs" c="dimmed">
          {t('registries.rc816.sumEmpty')}
        </Text>
      )}
      {state.skipped > 0 && (
        <Text size="xs" c="dimmed">
          {t('registries.rc816.sumSkipped', { count: state.skipped })}
        </Text>
      )}
    </Group>
  );
}
