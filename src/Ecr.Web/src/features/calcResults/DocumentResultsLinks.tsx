import type { JSX } from 'react';
import { Link } from 'react-router-dom';
import { Badge, Button, Group, Menu } from '@mantine/core';
import { formatPeriodKey } from '@/shared/format';
import { t } from '@/shared/i18n';

/** Адреса огляду «Calculation results» документа за рік (з «місяцем результатів» річної розкладки). */
export function resultsHref(documentId: number, periodKey: number, yearLayout: boolean): string {
  const year = Math.floor(periodKey / 100);
  const month = periodKey % 100;
  return `/documents/${String(documentId)}/results?year=${String(year)}${yearLayout ? `&month=${String(month)}` : ''}`;
}

/**
 * «All months» у смузі таблиці документа (обидві розкладки, CALC-RESULTS.md §2): усі розраховані числа документа за
 * рік. ⚠ Лише для `Calculation.View` — тим самим правом захищено `GET …/calculation-results/year`.
 */
export function AllMonthsLink({
  documentId,
  periodKey,
  yearLayout,
}: {
  readonly documentId: number;
  readonly periodKey: number;
  readonly yearLayout: boolean;
}): JSX.Element {
  return (
    <Button
      component={Link}
      to={resultsHref(documentId, periodKey, yearLayout)}
      variant="subtle"
      size="xs"
      title={t('calcResults.allMonthsHint', { year: Math.floor(periodKey / 100) })}
      data-testid="doc-cr-all"
    >
      {t('calcResults.allMonths')}
    </Button>
  );
}

/**
 * Річна розкладка (D-474, варіант A макета): замість перемикача місяця — «Year YYYY» і меню «Calculated: <місяць>».
 * Розраховані колонки сітки показують числа періоду, на якому відкрито документ, тож вибір місяця результатів —
 * це `?periodKey` місяця (вводи місяців у колонках однакові для будь-якого місяця року).
 */
export function YearLayoutChips({
  periodKey,
  periods,
  onPick,
}: {
  readonly periodKey: number;
  readonly periods: readonly number[] | undefined;
  readonly onPick: (periodKey: number) => void;
}): JSX.Element {
  const year = Math.floor(periodKey / 100);
  const months = (periods ?? []).filter((p) => Math.floor(p / 100) === year);
  return (
    <Group gap="xs" wrap="nowrap">
      <Badge variant="light" color="gray" data-testid="doc-cr-year" title={t('calcResults.yearChipHint')}>
        {t('calcResults.yearChip', { year })}
      </Badge>
      <Menu position="bottom-end" withinPortal>
        <Menu.Target>
          <Button variant="default" size="xs" data-testid="doc-cr-month">
            {t('calcResults.calculatedMonth', { month: formatPeriodKey(periodKey) })}
          </Button>
        </Menu.Target>
        <Menu.Dropdown>
          <Menu.Label>{t('calcResults.calculatedMonthHeading')}</Menu.Label>
          {months.map((p) => (
            <Menu.Item key={p} onClick={() => onPick(p)} aria-current={p === periodKey ? 'true' : undefined}>
              {formatPeriodKey(p)}
            </Menu.Item>
          ))}
        </Menu.Dropdown>
      </Menu>
    </Group>
  );
}
