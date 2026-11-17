import type { JSX } from 'react';
import { Link } from 'react-router-dom';
import { Badge, Button, Group } from '@mantine/core';
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
 * Річна розкладка (D-474, варіант A макета): замість перемикача місяця — «Year YYYY».
 *
 * ✎ LAND-YEAR L7a: меню «Calculated: <місяць>» прибрано — воно міняло `?periodKey`, тобто було тим самим
 * перемикачем місяця. Після L1/L2b розраховані місячні колонки показують числа місяця-власника (формули — комірки
 * екземпляра місяця, методології — `CalculatedCellOverlay` за власником) за будь-якого періоду перегляду, а сторінка
 * сама веде будь-який місяць на якір року (`yearAnchorOf`). Усі місяці результатів — «All months» (`AllMonthsLink`).
 */
export function YearLayoutChips({ periodKey }: { readonly periodKey: number }): JSX.Element {
  const year = Math.floor(periodKey / 100);
  return (
    <Group gap="xs" wrap="nowrap">
      <Badge variant="light" color="gray" data-testid="doc-cr-year" title={t('calcResults.yearChipHint')}>
        {t('calcResults.yearChip', { year })}
      </Badge>
    </Group>
  );
}
