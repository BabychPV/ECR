import { useEffect, type JSX } from 'react';
import { t } from '@/shared/i18n';
import { EcrApiError } from '@/api/client';
import { NoticeRow } from '@/features/documents/notices/NoticeRow';
import { publishNotice, withdrawNotice } from '@/features/documents/notices/noticeStore';
import {
  emphasizeNeedsInput,
  goToFirstNeedsInput,
  needsInputSummary,
  stepNeedsInput,
  useNeedsInputSummary,
} from '@/features/grid/needsInputStore';

/** Ідентифікатор повідомлення «Cannot submit…» у рядку повідомлень. */
export const CannotSubmitNoticeId = 'cannot-submit';

/**
 * ✎ DOCUX L9 (§4.5 плану, рішення людини Q6): «Submit» з порожніми обов'язковими комірками (шаблон і входи методологій)
 * запиту НЕ шле: фокус — на першу порожню, рамка 2 px на всіх, у рядку повідомлень — «Cannot submit: N required cells
 * empty · Go to next (F8)». Повідомлення знімається саме, щойно все заповнено.
 *
 * ⚠ Видно лише змонтовані сітки аркуша (`needsInputStore`); про ще не відкриті таблиці рядок каже прямо («N tables not
 * opened yet…»); остаточне слово — за сервером (Submit його перевіряє, L6).
 * ⚠ Річна розкладка (`month`): лише комірки місяця, що подається, і колонки без місяця.
 *
 * @returns `true` — подання зупинено (є що заповнити), `false` — можна подавати.
 */
export function guardSubmitNeedsInput(month: number | null = null): boolean {
  if (needsInputSummary(month).count === 0) return false;

  goToFirstNeedsInput(month);
  emphasizeNeedsInput(true);
  publishNotice({ id: CannotSubmitNoticeId, priority: 4, blocking: true, render: () => <CannotSubmitNotice month={month} /> });

  return true;
}

function CannotSubmitNotice({ month }: { readonly month: number | null }): JSX.Element | null {
  const { count, unmountedTables } = useNeedsInputSummary(month);

  useEffect(() => {
    if (count > 0) return;
    // Усе заповнено — повідомлення й підсилення знімаються (наступна спроба покаже їх знову).
    emphasizeNeedsInput(false);
    withdrawNotice(CannotSubmitNoticeId);
  }, [count]);

  if (count === 0) return null;

  return (
    <NoticeRow
      tone="error"
      text={
        unmountedTables > 0
          ? `${t('document.notices.cannotSubmitRequired', { count })} · ${t('grid.needsInput.otherTables', { count: unmountedTables })}`
          : t('document.notices.cannotSubmitRequired', { count })
      }
      testId="notice-cannot-submit"
      actions={[{ label: t('document.notices.goToNext'), keys: 'F8', run: () => void stepNeedsInput(1), testId: 'notice-cannot-submit-next' }]}
      onDismiss={() => {
        emphasizeNeedsInput(false);
        withdrawNotice(CannotSubmitNoticeId);
      }}
    />
  );
}

/** Ідентифікатор повідомлення «подання чекає власного перерахунку». */
export const SubmitWaitsRecalcNoticeId = 'submit-waits-recalc';

/** Відмова подання, бо власний перерахунок після правок ще йде (`ECR-SUB-4221` ownRecalculationPending, ворота Q2). */
export function isOwnRecalculationPending(error: unknown): boolean {
  return error instanceof EcrApiError && error.problem.extensions2?.['messageKey'] === 'err.ECR-SUB-4221.ownRecalculationPending';
}

/**
 * ✎ DOCUX (RC b18): людина правила й одразу подає — сервер свідомо чекає тихого перерахунку її правок (Q2). Це не тупик і
 * не помилка: рядок повідомлень каже, що відбувається (чип показує «Queued / Recalculating»), і дає «Submit again»;
 * знімається після успішного подання чи хрестиком.
 */
export function showSubmitWaitsRecalc(retry: () => void): void {
  publishNotice({
    id: SubmitWaitsRecalcNoticeId,
    priority: 4,
    blocking: true,
    render: () => (
      <NoticeRow
        tone="info"
        text={t('document.notices.submitWaitsRecalc')}
        testId="notice-submit-waits-recalc"
        actions={[{ label: t('document.notices.submitAgain'), run: retry, testId: 'notice-submit-again' }]}
        onDismiss={() => withdrawNotice(SubmitWaitsRecalcNoticeId)}
      />
    ),
  });
}
