import { useEffect, type JSX } from 'react';
import { Badge, Button, Group, Popover, Stack, Text, VisuallyHidden } from '@mantine/core';
import { useQueryClient } from '@tanstack/react-query';
import { formatDateTime, formatTime } from '@/shared/format/datetime';
import { formatMonthName } from '@/shared/format';
import { t } from '@/shared/i18n';
import { badgeLook } from '@/shared/ui/StatusBadge';
import { DisabledReason } from '@/features/common/DisabledReason';
import { calcStatusOf, type CalcStatusSource, type CalcStatusView, type LocalRecalc } from './calcStatusModel';

/** Як часто перечитується картка, поки задача в черзі чи рахується (стан — із сервера, переживає перезавантаження). */
export const CalcStatusPollMs = 3_000;

interface CalcStatusChipProps {
  readonly documentId: number;
  readonly periodKey: number;
  readonly source: CalcStatusSource | undefined;
  readonly local: LocalRecalc;
  /** «Recalculate» / «Retry» — та сама дія, що в «More» і F9; `null` — ролі її не дано. */
  readonly recalculate: { readonly run: () => void; readonly loading: boolean; readonly blockedReason: string | null } | null;
}

function monthsText(view: CalcStatusView, reason: 'closed' | 'approved'): string | null {
  const months = view.lockedMonths.filter((month) => month.reason === reason || (reason === 'closed' && month.reason === 'other'));
  if (months.length === 0) return null;
  const names = months.map((month) => formatMonthName(month.periodKey % 100).slice(0, 3)).join(', ');

  return reason === 'closed' ? t('calcStatus.lockedMonths', { months: names }) : t('calcStatus.approvedMonths', { months: names });
}

/**
 * ✎ DOCUX L4 (§3.2 плану; рішення людини Q2, Q3, Q5): ОДИН чип розрахунку в рядку стану — «Calculated 14:02» /
 * «Results out of date · Recalculate» / «Queued · position 3» / «Recalculating · Mar (3 of 9)» / «Recalculation failed ·
 * Retry». Закриті / затверджені застарілі місяці — сіра довідка поруч, не попередження. Подробиці — ⓘ.
 *
 * ⚠ Поки задача в черзі чи рахується, картка перечитується кожні 3 с (стан — сервера, тож переживає перезавантаження і
 * показує чужі й нічні задачі, коли контракт L3 їх віддає).
 */
export function CalcStatusChip({ documentId, periodKey, source, local, recalculate }: CalcStatusChipProps): JSX.Element | null {
  const queryClient = useQueryClient();
  const view = calcStatusOf(source, local);
  const { status } = view;
  const active = status.kind === 'queued' || status.kind === 'running';

  useEffect(() => {
    if (!active) return undefined;
    const timer = setInterval(() => {
      void queryClient.invalidateQueries({ queryKey: ['document', documentId, periodKey], exact: true });
    }, CalcStatusPollMs);

    return () => clearInterval(timer);
  }, [active, documentId, periodKey, queryClient]);

  const closedNote = monthsText(view, 'closed');
  const approvedNote = monthsText(view, 'approved');
  const notes = [closedNote, approvedNote].filter((note): note is string => note !== null);
  if (status.kind === 'unknown' && notes.length === 0) return null;

  const tone =
    status.kind === 'failed' ? 'danger' : status.kind === 'stale' ? 'warning' : status.kind === 'upToDate' ? 'neutral' : 'info';
  const look = badgeLook(tone);
  const label = (() => {
    switch (status.kind) {
      case 'upToDate':
        return `✓ ${t('calcStatus.upToDate', { time: status.at === null ? '' : formatTime(status.at) })}`;
      case 'stale':
        return `● ${t('calcStatus.stale')}`;
      case 'queued':
        return `◷ ${status.position === null ? t('calcStatus.queuedNoPosition') : t('calcStatus.queued', { position: status.position })}`;
      case 'running':
        return `⟳ ${
          status.done === null || status.total === null
            ? t('calcStatus.runningNoStep')
            : t('calcStatus.running', {
                month: status.periodKey === null ? '' : formatMonthName(status.periodKey % 100).slice(0, 3),
                done: status.done,
                total: status.total,
              })
        }`;
      case 'failed':
        return `✕ ${t('calcStatus.failed')}`;
      case 'unknown':
        return null;
    }
  })();
  const detail = (() => {
    switch (status.kind) {
      case 'stale':
        return status.since === null ? t('document.staleResults.hint') : t('document.staleResults.hintSince', { date: formatDateTime(status.since) });
      case 'failed':
        return [t('calcStatus.failedHint'), status.errorCode, status.correlationId].filter((part) => part !== null && part !== '').join(' · ');
      case 'queued':
        return status.estimatedStartAt === null ? t('calcStatus.queuedHint') : t('calcStatus.queuedEta', { time: formatTime(status.estimatedStartAt) });
      default:
        return null;
    }
  })();
  const action = status.kind === 'stale' ? t('workflow.recalculate') : status.kind === 'failed' ? t('calcStatus.retry') : null;

  return (
    <Group gap="xs" wrap="nowrap" data-testid="calc-status" data-calc-status={status.kind} aria-live="polite">
      {label !== null && (
        <Popover position="bottom-start" withArrow shadow="md" width={360} disabled={detail === null}>
          <Popover.Target>
            <Badge
              component="button"
              type="button"
              size="sm"
              variant="transparent"
              radius="xs"
              c={look.text}
              bg={look.bg}
              styles={{ root: { textTransform: 'none', letterSpacing: 'normal', fontWeight: 500, cursor: detail === null ? 'default' : 'pointer', border: `1px solid ${look.border}` } }}
              data-testid="calc-status-chip"
            >
              {label}
              {detail !== null && <VisuallyHidden>{` ${detail}`}</VisuallyHidden>}
            </Badge>
          </Popover.Target>
          {detail !== null && (
            <Popover.Dropdown>
              <Stack gap="xs">
                <Text size="sm" fw={600}>
                  {label}
                </Text>
                <Text size="sm">{detail}</Text>
              </Stack>
            </Popover.Dropdown>
          )}
        </Popover>
      )}
      {action !== null && recalculate !== null && (
        <DisabledReason reason={recalculate.blockedReason}>
          <Button size="xs" variant="subtle" loading={recalculate.loading} onClick={recalculate.run} aria-keyshortcuts="F9" data-testid="calc-status-action">
            {action}
          </Button>
        </DisabledReason>
      )}
      {notes.map((note) => (
        <Text key={note} size="xs" c="dimmed" data-testid="calc-status-locked">
          {`ⓘ ${note}`}
        </Text>
      ))}
    </Group>
  );
}

