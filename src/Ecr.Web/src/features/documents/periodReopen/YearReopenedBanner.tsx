import type { JSX } from 'react';
import { Alert, List } from '@mantine/core';
import type { components } from '@/api/schema';
import { formatPeriodKey } from '@/shared/format';
import { t } from '@/shared/i18n';
import { untilTextInZone } from '@/shared/siteMoment';

type ReopenedPeriod = components['schemas']['DocumentReopenedPeriod'];

export interface YearReopenedBannerProps {
  /** Діючі винятки «місяць відкрито для документа» за рік перегляду (`reopenedPeriods` картки, за зростанням). */
  readonly periods: readonly ReopenedPeriod[];
  readonly timeZoneId?: string | null | undefined;
}

/**
 * ✎ L7c (F13 AU до L7ab): документ «рік» (Land = річний шаблон, усі місяці в одному документі) — банер відкритих
 * для ЦЬОГО документа місяців показує КОЖЕН відкритий місяць року з його строком, а не лише якір (січень).
 * Порожній перелік — банера немає. Строк — у поясі майданчика, як у місячного банера.
 */
export function YearReopenedBanner({ periods, timeZoneId }: YearReopenedBannerProps): JSX.Element | null {
  if (periods.length === 0) return null;

  return (
    <Alert color="brand" variant="light" role="status" data-testid="document-year-reopened">
      <List size="sm" spacing={2}>
        {periods.map((p) => (
          <List.Item key={p.periodKey} data-year-reopened-month={p.periodKey}>
            {t('document.reopen.done', {
              period: formatPeriodKey(p.periodKey),
              until: untilTextInZone(p.openUntil, timeZoneId),
            })}
          </List.Item>
        ))}
      </List>
    </Alert>
  );
}
