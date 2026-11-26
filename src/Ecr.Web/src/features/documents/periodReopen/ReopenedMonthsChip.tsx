import { useId, type JSX } from 'react';
import { Badge, Tooltip, VisuallyHidden } from '@mantine/core';
import { formatPeriodKey } from '@/shared/format';
import { t } from '@/shared/i18n';
import { badgeLook } from '@/shared/ui/StatusBadge';
import { untilTextInZone } from '@/shared/siteMoment';
import { reopenedRanges, type ReopenedMonth } from './reopenedRanges';

export interface ReopenedMonthsChipProps {
  /** Діючі винятки «місяць відкрито для документа» (`reopenedPeriods` картки, за зростанням). */
  readonly periods: readonly ReopenedMonth[];
  readonly timeZoneId?: string | null | undefined;
}

/**
 * ✎ b14 doc-notices-compact (людина 25.11, doc 96 після перевідкриття січ–вер): замість банера-списку з 9 рядків над
 * сіткою — ОДИН чип у шапці: «Reopened: Jan–Sep 2026 · until Nov 25, 11:59 PM». Суміжні місяці з однаковим строком —
 * діапазон, різні строки — кілька діапазонів через «;». Повний перелік — у підказці (і для читалки — у описі чипа).
 *
 * ⚠ Чип — кнопка без дії (`aria-describedby` на перелік): підказку Mantine показує й на фокус клавіатурою, а не лише
 * мишею. Порожній перелік — чипа немає. Висота — висота бейджа (не зсуває сітку).
 */
export function ReopenedMonthsChip({ periods, timeZoneId }: ReopenedMonthsChipProps): JSX.Element | null {
  const listId = useId();
  if (periods.length === 0) return null;

  const until = (openUntil: string): string => untilTextInZone(openUntil, timeZoneId);
  const ranges = reopenedRanges(periods, until);
  const label = t('document.notices.reopened', {
    ranges: ranges.map((range) => t('document.notices.reopenedRange', { months: range.months, until: range.until })).join('; '),
  });
  const lines = [...periods]
    .sort((a, b) => a.periodKey - b.periodKey)
    .map((period) => t('document.reopen.done', { period: formatPeriodKey(period.periodKey), until: until(period.openUntil) }));
  const look = badgeLook('info');

  return (
    <>
      <Tooltip label={lines.join('\n')} multiline maw={360} style={{ whiteSpace: 'pre-line' }} withinPortal>
        <Badge
          component="button"
          type="button"
          size="sm"
          variant="transparent"
          radius="xs"
          c={look.text}
          bg={look.bg}
          styles={{
            root: {
              height: 22,
              border: `1px solid ${look.border}`,
              textTransform: 'none',
              letterSpacing: 'normal',
              fontWeight: 500,
              cursor: 'default',
              maxWidth: 460,
            },
            label: { overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' },
          }}
          aria-describedby={listId}
          data-testid="document-reopened-chip"
          data-reopened-ranges={ranges.length}
        >
          {label}
        </Badge>
      </Tooltip>
      <VisuallyHidden id={listId}>
        <ul>
          {lines.map((line) => (
            <li key={line}>{line}</li>
          ))}
        </ul>
      </VisuallyHidden>
    </>
  );
}
