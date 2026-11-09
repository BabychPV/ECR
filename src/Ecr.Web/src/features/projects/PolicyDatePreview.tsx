import type { JSX } from 'react';
import { Stack, Text } from '@mantine/core';
import type { PeriodCalendarDto } from '@/api/types';
import { formatDate, todayDateOnly } from '@/shared/format';
import { formatPeriodKey } from '@/shared/format/periodKey';
import { t } from '@/shared/i18n';
import {
  policyDates,
  previewPeriods,
  projectEndOf,
  samplePeriods,
  utcMidnight,
  yearEditsUntil,
  type PolicyOffsets,
} from './policyPreviewDates';

/** Дата `yyyy-MM-dd` (опівніч майданчика) мовою продукту — сама ця дата, без зсуву поясом браузера. */
function dateText(isoDate: string): string {
  const at = utcMidnight(isoDate);
  return at === null ? isoDate : formatDate(at, { dateStyle: 'medium', timeZone: 'UTC' });
}

/**
 * ✎ Приймання 08.11, f5: живе прев'ю дат політики — для поточного й наступного періоду проєкту, з вкладки Settings
 * якого відкрито діалог; без проєкту — на зразковому місяці з явним підписом. Правило дат — `policyPreviewDates.ts`
 * (дзеркало `Period.RecomputeBoundaries`).
 */
export function PolicyDatePreview({
  offsets,
  calendar,
  projectTitle,
  sampleTitle,
  id,
}: {
  readonly offsets: PolicyOffsets;
  /** Календар проєкту; без нього (або порожній) — зразковий місяць. */
  readonly calendar?: PeriodCalendarDto | undefined;
  /** Підпис, коли дати — з календаря проєкту. */
  readonly projectTitle: string;
  /** Підпис для зразкового місяця — каже, що це зразок. */
  readonly sampleTitle: string;
  readonly id: string;
}): JSX.Element {
  const fromProject = calendar === undefined ? [] : previewPeriods(calendar);
  const useProject = fromProject.length > 0;
  const sample = samplePeriods(todayDateOnly());
  const periods = useProject ? fromProject : sample.periods;
  const projectEnd = (useProject && calendar !== undefined ? projectEndOf(calendar) : null) ?? sample.projectEnd;
  const kind = useProject ? calendar?.periodKind : 'Monthly';
  const title = useProject ? projectTitle : sampleTitle;

  return (
    <Stack gap="xs" mt="xs" id={id} aria-live="polite" data-policy-preview={useProject ? 'project' : 'sample'}>
      <Text size="xs" fw={600}>
        {title}
      </Text>
      {periods.map((period) => {
        const dates = policyDates(period, offsets);
        return (
          <Text size="xs" key={period.periodKey} data-policy-preview-period={period.periodKey}>
            {t('periods.policyPreview.period', {
              period: formatPeriodKey(period.periodKey, kind),
              opens: dateText(dates.opensOn),
              late: dateText(dates.lateFrom),
              locks: dateText(dates.locksOn),
            })}
          </Text>
        );
      })}
      <Text size="xs" data-policy-preview-year="">
        {t('periods.policyPreview.year', { date: dateText(yearEditsUntil(projectEnd, offsets)) })}
      </Text>
      {offsets.graceOffsetDays > offsets.hardCloseOffsetDays && (
        <Text size="xs" c="statusWarning" data-policy-preview-invalid="">
          {t('periods.policyPreview.orderInvalid')}
        </Text>
      )}
    </Stack>
  );
}
