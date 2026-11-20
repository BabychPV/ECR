import type { JSX } from 'react';
import { Alert, Text } from '@mantine/core';
import { periodLabel } from '@/features/workflow/SheetActions';
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
  /** Приймальна 08.11 (i1): дія закритого періоду для `DocumentLockBanner`. */
  readonly periodAction?: JSX.Element | null;
  /** ✎ LAND-YEAR L7a: документ «рік» — ширші причини банера називають «Year YYYY» (`DocumentLockBanner`). */
  readonly yearLayout?: boolean;
  /**
   * ✎ D-6: документ «рік» — поданий чи затверджений МІСЯЦЬ, про який банер (ціль рішення людини, інакше найраніший
   * поданий / затверджений); `canDecide` — ця людина його вирішує. `null` — такого місяця немає (чи ще невідомо).
   * `undefined` — не річна розкладка: банер про аркуш за період сторінки, як було.
   */
  readonly monthTarget?:
    | { readonly periodKey: number; readonly state: 'Submitted' | 'Approved'; readonly canDecide: boolean }
    | null
    | undefined;
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
  periodAction = null,
  yearLayout = false,
  monthTarget,
}: DocumentSheetBannerProps): JSX.Element | null {
  // ✎ D-6: річна розкладка — стан свій у кожного місяця: банер каже про МІСЯЦЬ, а не «аркуш поданий, правка закрита».
  if (monthTarget !== undefined && !widerLock(lock)) {
    const rejected = state === 'Rejected';
    if (!rejected && monthTarget === null) return null;

    return (
      <>
        {/* Відхилений якір лишається видимим разом із причиною — навіть коли інший місяць поданий. */}
        {rejected && (
          <SheetStateBanner
            documentId={documentId}
            periodKey={periodKey}
            sheetCode={sheetCode}
            sheetCopyId={sheetCopyId}
            state="Rejected"
            canDecide={false}
            month
          />
        )}
        {monthTarget !== null && (
          <SheetStateBanner
            documentId={documentId}
            periodKey={monthTarget.periodKey}
            sheetCode={sheetCode}
            sheetCopyId={sheetCopyId}
            state={monthTarget.state}
            canDecide={monthTarget.canDecide}
            // Позначка «перераховано після подання» — про якір (картка документа), тож лише коли банер про якір.
            recalculatedAfterSubmission={recalculatedAfterSubmission && monthTarget.periodKey === periodKey}
            month
          />
        )}
      </>
    );
  }

  const contextual =
    !widerLock(lock) && (lock === 'sheetSubmitted' || lock === 'sheetApproved' || state === 'Rejected');

  if (!contextual) {
    return <DocumentLockBanner lock={lock} periodKey={periodKey} periodAction={periodAction} yearLayout={yearLayout} />;
  }

  return (
    <SheetStateBanner
      documentId={documentId}
      periodKey={periodKey}
      sheetCode={sheetCode}
      sheetCopyId={sheetCopyId}
      state={lock === 'sheetSubmitted' ? 'Submitted' : lock === 'sheetApproved' ? 'Approved' : 'Rejected'}
      canDecide={canDecide}
      recalculatedAfterSubmission={recalculatedAfterSubmission}
      month={false}
    />
  );
}

/**
 * Банер одного стану аркуша за період: хто й коли (журнал переходів цього періоду), що далі цій ролі.
 * ✎ D-6: `month` — річна розкладка: заголовок називає місяць, а текст каже про колонку місяця, а не про весь аркуш.
 */
function SheetStateBanner({
  documentId,
  periodKey,
  sheetCode,
  sheetCopyId,
  state,
  canDecide,
  recalculatedAfterSubmission = false,
  month,
}: {
  readonly documentId: number;
  readonly periodKey: number;
  readonly sheetCode: string;
  readonly sheetCopyId: number | null;
  readonly state: 'Submitted' | 'Approved' | 'Rejected';
  readonly canDecide: boolean;
  readonly recalculatedAfterSubmission?: boolean;
  readonly month: boolean;
}): JSX.Element {
  const history = useWorkflowHistory(documentId, periodKey, true, sheetCopyId);
  const period = periodLabel(periodKey);
  // ✎ D-6: «Submitted 17 Sep by D. Akhmetova · April 2026» — місяць у заголовку, щоб не читалося як про якір.
  const monthSuffix = month ? ` · ${period}` : '';

  const event = latestInto(history.data, sheetCode, sheetCopyId, state);
  const who = event === null ? null : { name: event.byDisplayName, date: formatDate(event.at) };
  const lock = state === 'Submitted' ? 'sheetSubmitted' : state === 'Approved' ? 'sheetApproved' : undefined;

  if (state === 'Submitted') {
    return (
      <Alert
        color="statusWarning"
        variant="light"
        role="status"
        data-document-lock={lock}
        data-testid="document-sheet-banner"
        data-banner-period={month ? periodKey : undefined}
        title={(who === null ? t('document.banner.submitted') : t('document.banner.submittedBy', who)) + monthSuffix}
      >
        {/* ⚠ Речення посібника — окремим текстом, крок — окремим елементом:
            людина (і тест) знаходить те саме речення, що в розділі 5.3. */}
        {month ? t('document.banner.monthSubmitted', { period }) : t('grid.submittedReadOnlyHint')}{' '}
        <span>{canDecide ? t('document.banner.submittedDecide') : t('document.banner.submittedWait')}</span>
        {recalculatedAfterSubmission && (
          <Text component="strong" display="block" fw={700} mt="xs" size="sm" data-recalculated-after-submission="">
            {t('sheet.recalculatedAfterSubmission')}: {t('sheet.recalculatedAfterSubmissionHint')}
          </Text>
        )}
      </Alert>
    );
  }

  if (state === 'Approved') {
    return (
      <Alert
        color="statusWarning"
        variant="light"
        role="status"
        data-document-lock={lock}
        data-testid="document-sheet-banner"
        data-banner-period={month ? periodKey : undefined}
        title={(who === null ? t('document.banner.approved') : t('document.banner.approvedBy', who)) + monthSuffix}
      >
        {month ? t('document.banner.monthApproved', { period }) : t('document.lock.sheetApproved')}
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
      data-banner-period={month ? periodKey : undefined}
      title={(who === null ? t('document.banner.rejected') : t('document.banner.rejectedBy', who)) + monthSuffix}
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
