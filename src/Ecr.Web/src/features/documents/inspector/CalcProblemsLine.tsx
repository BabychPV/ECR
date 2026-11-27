import { useMemo, type JSX } from 'react';
import { Anchor, Group, Text } from '@mantine/core';
import type { DocumentTableDto } from '@/api/types';
import { t } from '@/shared/i18n';
import { summarizeCalcIssues } from '../calcIssuesStrip/calcIssuesStripModel';
import type { MonthUnmatchedRows } from '../useUnmatchedRows';

/**
 * ✎ DOCUX (огляд UI 27.11): Issues казав лише «Not validated yet — Run Validate…», а рядок повідомлень у ту саму мить —
 * «Calculation finished with problems…». Тепер Issues називає проблеми розрахунку (те саме число, що сума причин смуги
 * `CalculationIssuesStrip`, без відсіяних фільтром активності) і веде у вкладку Status, де їхній повний перелік.
 */
export function calcProblemTotal(months: readonly MonthUnmatchedRows[], tables: readonly DocumentTableDto[]): number {
  return summarizeCalcIssues(months, tables)
    .reasons.filter((reason) => reason.kind !== 'inactive')
    .reduce((sum, reason) => sum + reason.total, 0);
}

export function CalcProblemsLine({
  months,
  tables,
  onOpen,
}: {
  readonly months: readonly MonthUnmatchedRows[] | undefined;
  readonly tables: readonly DocumentTableDto[];
  readonly onOpen: () => void;
}): JSX.Element | null {
  const total = useMemo(() => (months === undefined ? 0 : calcProblemTotal(months, tables)), [months, tables]);
  if (total === 0) return null;

  return (
    <Group gap="xs" px="sm" pt="sm" wrap="wrap" data-inspector-calc-problems={total}>
      <Text size="sm">{t('inspector.calcProblems', { count: total })}</Text>
      <Anchor component="button" type="button" size="sm" onClick={onOpen} data-inspector-calc-problems-open="">
        {t('inspector.calcProblemsOpen')}
      </Anchor>
    </Group>
  );
}
