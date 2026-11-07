import type { JSX } from 'react';
import { Alert, Text } from '@mantine/core';
import { isEventOfSheet, useWorkflowHistory, type WorkflowEvent } from '@/features/workflow/api';
import { formatDate } from '@/shared/format/datetime';
import { t } from '@/shared/i18n';
import { DocumentLockBanner } from './DocumentLockBanner';
import type { DocumentLock } from './documentLock';

interface DocumentSheetBannerProps {
  readonly documentId: number;
  readonly periodKey: number;
  /** Код активного (видимого цій ролі) аркуша. */
  readonly sheetCode: string;
  /** ✎ RS: Id копії активного аркуша; `null`/не задано — оригінал. */
  readonly sheetCopyId?: number | null;
  /** Стан активного аркуша за період. */
  readonly state: string;
  readonly lock: DocumentLock | null;
  /** Ця людина може затвердити чи відхилити поданий аркуш. */
  readonly canDecide: boolean;
  /**
   * ✎ UAT-recalc-ux: поданий аркуш перераховано ПІСЛЯ подання (GET /documents/{id} → sheets[].recalculatedAfterSubmission):
   * погоджувач має знати, що числа зрушили. Зникає з повторним поданням або затвердженням.
   */
  readonly recalculatedAfterSubmission?: boolean;
}

/**
 * Банер стану аркуша з контекстом «хто, коли, що далі» (UI-26; макет
 * `docs/design/hybrid/screen-document.js`, `renderBanner`): «Submitted
 * 17 Sep 2026 by D. Akhmetova» і наступний крок для цієї ролі.
 *
 * ⚠ Ширші причини (`F-18`: архівний проєкт, закритий чи не відкритий період)
 * і аркуші без контексту — як і раніше `DocumentLockBanner`: вони важливіші
 * за стан аркуша. Текст поданого й затвердженого аркуша лишається тим самим
 * реченням із посібника (розділ 5.3), до нього лише додано заголовок і крок.
 *
 * ⛔ Хто й коли — лише з журналу переходів (`BE-11`) ЦЬОГО аркуша: найновіша
 * подія з `toState === state`. Журналу немає (вантажиться, відмова, порожній)
 * — заголовок без «хто/коли», а не вигадане ім'я. Код — лише активного
 * видимого аркуша, тож аркуш, прихований від ролі, сюди не потрапляє.
 */
export function DocumentSheetBanner({
  documentId,
  periodKey,
  sheetCode,
  sheetCopyId = null,
  state,
  lock,
  canDecide,
  recalculatedAfterSubmission = false,
}: DocumentSheetBannerProps): JSX.Element | null {
  const contextual =
    !widerLock(lock) && (lock === 'sheetSubmitted' || lock === 'sheetApproved' || state === 'Rejected');
  const history = useWorkflowHistory(documentId, periodKey, contextual, sheetCopyId);

  if (!contextual) return <DocumentLockBanner lock={lock} periodKey={periodKey} />;

  const event = latestInto(history.data, sheetCode, sheetCopyId, state);
  const who = event === null ? null : { name: event.byDisplayName, date: formatDate(event.at) };

  if (lock === 'sheetSubmitted') {
    return (
      <Alert
        color="statusWarning"
        variant="light"
        role="status"
        data-document-lock={lock}
        data-testid="document-sheet-banner"
        title={who === null ? t('document.banner.submitted') : t('document.banner.submittedBy', who)}
      >
        {/* ⚠ Речення посібника — окремим текстом, крок — окремим елементом:
            людина (і тест) знаходить те саме речення, що в розділі 5.3. */}
        {t('grid.submittedReadOnlyHint')}{' '}
        <span>{canDecide ? t('document.banner.submittedDecide') : t('document.banner.submittedWait')}</span>
        {recalculatedAfterSubmission && (
          <Text component="strong" display="block" fw={700} mt="xs" size="sm" data-recalculated-after-submission="">
            {t('sheet.recalculatedAfterSubmission')}: {t('sheet.recalculatedAfterSubmissionHint')}
          </Text>
        )}
      </Alert>
    );
  }

  if (lock === 'sheetApproved') {
    return (
      <Alert
        color="statusWarning"
        variant="light"
        role="status"
        data-document-lock={lock}
        data-testid="document-sheet-banner"
        title={who === null ? t('document.banner.approved') : t('document.banner.approvedBy', who)}
      >
        {t('document.lock.sheetApproved')}
      </Alert>
    );
  }

  // Rejected: аркуш знову редагується — банер каже причину й що робити далі.
  const reason = event?.reason ?? '';

  return (
    <Alert
      color="statusError"
      variant="light"
      role="status"
      data-testid="document-sheet-banner"
      title={who === null ? t('document.banner.rejected') : t('document.banner.rejectedBy', who)}
    >
      {reason.length === 0 ? t('document.banner.rejectedNext') : `«${reason}» ${t('document.banner.rejectedNext')}`}
    </Alert>
  );
}

/** Причина ширша за стан аркуша (`F-18`). */
function widerLock(lock: DocumentLock | null): boolean {
  return lock === 'projectArchived' || lock === 'periodClosed' || lock === 'periodNotOpen';
}

/** Найновіша подія переходу аркуша в цей стан (журнал — найновіші перші). */
function latestInto(
  events: readonly WorkflowEvent[] | undefined,
  sheetCode: string,
  sheetCopyId: number | null,
  state: string,
): WorkflowEvent | null {
  // ⚠ Відповідь не масив (старіший сервер, заглушка) — журналу немає.
  if (!Array.isArray(events)) return null;

  return events.find((event) => isEventOfSheet(event, sheetCode, sheetCopyId) && event.toState === state) ?? null;
}
