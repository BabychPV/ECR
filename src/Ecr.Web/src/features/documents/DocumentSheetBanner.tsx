import { useId, type JSX } from 'react';
import { Alert, Badge, Group, Popover, Stack, Text, VisuallyHidden } from '@mantine/core';
import { badgeLook } from '@/shared/ui/StatusBadge';
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
  /**
   * ✎ DOCUX L8 (рішення людини Q7): постійні стани «Submitted / Approved … by …» — ЧИП у рядку стану (`chips`), а не банер;
   * банери, що потребують дії (ширші блокування, «Rejected» з причиною), — під рядком дій (`alerts`). `all` — обидва.
   */
  readonly part?: 'all' | 'chips' | 'alerts';
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
  part = 'all',
}: DocumentSheetBannerProps): JSX.Element | null {
  const chips = part !== 'alerts';
  const alerts = part !== 'chips';
  // ✎ D-6: річна розкладка — стан свій у кожного місяця: банер каже про МІСЯЦЬ, а не «аркуш поданий, правка закрита».
  if (monthTarget !== undefined && !widerLock(lock)) {
    const rejected = state === 'Rejected';
    if (!rejected && monthTarget === null) return null;

    return (
      <>
        {/* Відхилений якір лишається видимим разом із причиною — навіть коли інший місяць поданий. */}
        {rejected && alerts && (
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
        {monthTarget !== null && chips && (
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
    if (!alerts) return null;
    return <DocumentLockBanner lock={lock} periodKey={periodKey} periodAction={periodAction} yearLayout={yearLayout} />;
  }

  const contextualState = lock === 'sheetSubmitted' ? 'Submitted' : lock === 'sheetApproved' ? 'Approved' : 'Rejected';
  if (contextualState === 'Rejected' ? !alerts : !chips) return null;

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

  // ✎ DOCUX L8 (Q7): поданий / затверджений — ЧИП «Submitted 24 Nov by Ivan P.» з поясненням у спливаючому вікні (і для
  // читалки — прихованим текстом), а не банер на 2–3 рядки: це постійний стан, дії тут немає (вона — у кнопках).
  if (state === 'Submitted' || state === 'Approved') {
    const title =
      state === 'Submitted'
        ? (who === null ? t('document.banner.submitted') : t('document.banner.submittedBy', who)) + monthSuffix
        : (who === null ? t('document.banner.approved') : t('document.banner.approvedBy', who)) + monthSuffix;
    const detail =
      state === 'Submitted'
        ? [
            month ? t('document.banner.monthSubmitted', { period }) : t('grid.submittedReadOnlyHint'),
            canDecide ? t('document.banner.submittedDecide') : t('document.banner.submittedWait'),
          ]
        : [month ? t('document.banner.monthApproved', { period }) : t('document.lock.sheetApproved')];

    return (
      <SheetStateChip
        state={state}
        title={title}
        detail={detail}
        lock={lock}
        bannerPeriod={month ? periodKey : undefined}
        recalculatedAfterSubmission={state === 'Submitted' && recalculatedAfterSubmission}
      />
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

/**
 * ✎ DOCUX L8: чип стану аркуша в рядку стану — заголовок «хто й коли», ⓘ відкриває пояснення й наступний крок; позначка
 * «перераховано після подання» — окремим чипом поруч (для погоджувача). Тон — `badgeLook` (`StatusBadge`), колір лише
 * разом зі словом.
 */
function SheetStateChip({
  state,
  title,
  detail,
  lock,
  bannerPeriod,
  recalculatedAfterSubmission,
}: {
  readonly state: 'Submitted' | 'Approved';
  readonly title: string;
  /** Речення посібника й наступний крок — окремими реченнями (людина й тест знаходять кожне ціле). */
  readonly detail: readonly string[];
  readonly lock: string | undefined;
  readonly bannerPeriod: number | undefined;
  readonly recalculatedAfterSubmission: boolean;
}): JSX.Element {
  const look = badgeLook(state === 'Submitted' ? 'warning' : 'info');
  const warn = badgeLook('warning');
  // ✎ DOCUX (RC b18, keyboardPath): пояснення — ОПИС кнопки (`aria-describedby`), а не частина її назви: інакше назва чипа
  // («… approve or reject») збігалася з дією «Approve», і клавіатурний шлях потрапляв у чип замість кнопки.
  const detailId = useId();

  return (
    <Group gap="xs" wrap="nowrap" data-testid="document-sheet-banner" data-document-lock={lock} data-banner-period={bannerPeriod} data-sheet-state-chip={state}>
      <Popover position="bottom-start" withArrow shadow="md" width={360}>
        <Popover.Target>
          <Badge
            component="button"
            type="button"
            size="sm"
            variant="transparent"
            radius="xs"
            c={look.text}
            bg={look.bg}
            styles={{ root: { textTransform: 'none', letterSpacing: 'normal', fontWeight: 500, cursor: 'pointer' } }}
            aria-describedby={detailId}
          >
            {`${title} ⓘ`}
          </Badge>
        </Popover.Target>
        <Popover.Dropdown>
          <Stack gap="xs">
            <Text size="sm" fw={600}>
              {title}
            </Text>
            {detail.map((part) => (
              <Text key={part} size="sm">
                {part}
              </Text>
            ))}
          </Stack>
        </Popover.Dropdown>
      </Popover>
      <VisuallyHidden id={detailId}>
        {detail.map((part) => (
          <span key={part}> {part}</span>
        ))}
      </VisuallyHidden>
      {recalculatedAfterSubmission && (
        <Badge
          size="sm"
          variant="transparent"
          radius="xs"
          c={warn.text}
          bg={warn.bg}
          styles={{ root: { textTransform: 'none', letterSpacing: 'normal', fontWeight: 700 } }}
          title={t('sheet.recalculatedAfterSubmissionHint')}
          data-recalculated-after-submission=""
        >
          {t('sheet.recalculatedAfterSubmission')}
          <VisuallyHidden>{`: ${t('sheet.recalculatedAfterSubmissionHint')}`}</VisuallyHidden>
        </Badge>
      )}
    </Group>
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
