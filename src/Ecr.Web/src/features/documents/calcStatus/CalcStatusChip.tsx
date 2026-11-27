import { useId, type JSX } from 'react';
import { Badge, Button, Group, Popover, Stack, Text, VisuallyHidden } from '@mantine/core';
import { formatDateTime, formatTime } from '@/shared/format/datetime';
import { formatMonthName } from '@/shared/format';
import { t } from '@/shared/i18n';
import { badgeLook } from '@/shared/ui/StatusBadge';
import { DisabledReason } from '@/features/common/DisabledReason';
import { calcStatusOf, type CalcStatusView, type CardStaleness, type JobOrigin, type LocalRecalc, type LockedReason } from './calcStatusModel';
import { useDocumentCalcStatus } from './useDocumentCalcStatus';

export { CalcStatusPollMs } from './useDocumentCalcStatus';

interface CalcStatusChipProps {
  readonly documentId: number;
  readonly periodKey: number;
  /** Запас із картки (до відповіді `calc-status`). */
  readonly card: CardStaleness;
  readonly local: LocalRecalc;
  /** «Recalculate» / «Retry» — та сама дія, що в «More» і F9; `null` — ролі її не дано. */
  readonly recalculate: { readonly run: () => void; readonly loading: boolean; readonly blockedReason: string | null } | null;
  /**
   * ✎ DOCUX (живий прохід 27.11): у рядку повідомлень є проблеми розрахунку (рядки не пораховано, помилки формул). Прогін
   * ЗАВЕРШИВСЯ, але не чисто: чип «Calculated with issues» (тон попередження) замість «✓ Calculated» — і рядок, і чип кажуть
   * одне.
   */
  readonly issues?: boolean;
}

const short = (periodKey: number): string => formatMonthName(periodKey % 100).slice(0, 3);

/** Довідка про застарілі місяці, яких перерахувати не можна (Q3) — ключі літералами. */
function lockedNote(reason: LockedReason, months: string): string {
  switch (reason) {
    case 'approved':
      return t('calcStatus.approvedMonths', { months });
    case 'notOpen':
      return t('calcStatus.notOpenMonths', { months });
    case 'archived':
      return t('calcStatus.archivedMonths', { months });
    case 'closed':
      return t('calcStatus.lockedMonths', { months });
  }
}

function notesOf(view: CalcStatusView): string[] {
  const reasons: LockedReason[] = ['closed', 'notOpen', 'approved', 'archived'];
  return reasons.flatMap((reason) => {
    const months = view.lockedMonths.filter((month) => month.reason === reason).map((month) => short(month.periodKey));
    return months.length === 0 ? [] : [lockedNote(reason, months.join(', '))];
  });
}

/** Хто поставив задачу: «· automatic», «· after a source change», «· scheduled», «· started by X». */
function originText(origin: JobOrigin | null): string {
  if (origin === null) return '';
  if (origin.kind === 'auto') return ` · ${t('calcStatus.kind.auto')}`;
  if (origin.kind === 'cascade') return ` · ${t('calcStatus.kind.cascade')}`;
  if (origin.kind === 'scheduled') return ` · ${t('calcStatus.kind.scheduled')}`;
  if (!origin.own && origin.by !== null) return ` · ${t('calcStatus.startedBy', { name: origin.by })}`;
  return '';
}

/**
 * ✎ DOCUX L4 (§3.2 плану; рішення людини Q2, Q3, Q5): ОДИН чип розрахунку в рядку стану — «Calculated 14:02» /
 * «Results out of date · Recalculate» / «Queued · position 3» / «Recalculating · Mar (3 of 9) · automatic» /
 * «Recalculation failed · Retry» (код і кореляція для підтримки — в ⓘ). Закриті / затверджені / не відкриті застарілі
 * місяці — сіра довідка поруч, не попередження.
 *
 * Стан — із сервера (`calc-status`): переживає перезавантаження, показує свої й чужі задачі, тихий автоперерахунок (Q2).
 */
export function CalcStatusChip({ documentId, periodKey, card, local, recalculate, issues = false }: CalcStatusChipProps): JSX.Element | null {
  const server = useDocumentCalcStatus(documentId, periodKey, local.queued || local.running);
  // Пояснення — опис чипа (`aria-describedby`), не частина назви: «Recalculate» лишається назвою лише кнопки дії.
  const detailId = useId();
  const view = calcStatusOf(server, card, local);
  const { status } = view;

  const notes = notesOf(view);
  if (status.kind === 'unknown' && notes.length === 0) return null;

  // ✎ DOCUX: актуально, але з проблемами розрахунку — попередження, а не нейтральне «✓».
  const withIssues = status.kind === 'upToDate' && issues;
  const tone =
    status.kind === 'failed'
      ? 'danger'
      : status.kind === 'stale' || withIssues
        ? 'warning'
        : status.kind === 'upToDate'
          ? 'neutral'
          : 'info';
  const look = badgeLook(tone);
  const label = (() => {
    switch (status.kind) {
      case 'upToDate': {
        const time = status.at === null ? '' : formatTime(status.at);
        return withIssues ? `⚠ ${t('calcStatus.upToDateIssues', { time })}` : `✓ ${t('calcStatus.upToDate', { time })}`;
      }
      case 'stale':
        return `● ${t('calcStatus.stale')}`;
      case 'queued':
        return `◷ ${status.position === null ? t('calcStatus.queuedNoPosition') : t('calcStatus.queued', { position: status.position })}${originText(status.origin)}`;
      case 'running':
        return `⟳ ${
          status.done === null || status.total === null
            ? t('calcStatus.runningNoStep')
            : t('calcStatus.running', {
                month: status.periodKey === null ? '' : short(status.periodKey),
                done: status.done,
                total: status.total,
              })
        }${originText(status.origin)}`;
      case 'failed':
        return `✕ ${t('calcStatus.failed')}${originText(status.origin)}`;
      case 'unknown':
        return null;
    }
  })();
  const detail = (() => {
    switch (status.kind) {
      case 'upToDate':
        return withIssues ? t('calcStatus.issuesHint') : null;
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
    <Group
      gap="xs"
      wrap="nowrap"
      data-testid="calc-status"
      data-calc-status={status.kind}
      data-calc-issues={withIssues || undefined}
      aria-live="polite"
    >
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
              aria-describedby={detail === null ? undefined : detailId}
            >
              {label}
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
      {detail !== null && <VisuallyHidden id={detailId}>{detail}</VisuallyHidden>}
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
