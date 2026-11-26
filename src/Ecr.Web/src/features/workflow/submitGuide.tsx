import { useEffect, type JSX } from 'react';
import { t } from '@/shared/i18n';
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
 * ⚠ Видно лише змонтовані сітки аркуша (`needsInputStore`); остаточне слово — за сервером (Submit його перевіряє, L6).
 *
 * @returns `true` — подання зупинено (є що заповнити), `false` — можна подавати.
 */
export function guardSubmitNeedsInput(): boolean {
  if (needsInputSummary().count === 0) return false;

  goToFirstNeedsInput();
  emphasizeNeedsInput(true);
  publishNotice({ id: CannotSubmitNoticeId, priority: 4, render: () => <CannotSubmitNotice /> });

  return true;
}

function CannotSubmitNotice(): JSX.Element | null {
  const { count } = useNeedsInputSummary();

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
      text={t('document.notices.cannotSubmitRequired', { count })}
      testId="notice-cannot-submit"
      actions={[{ label: t('document.notices.goToNext'), keys: 'F8', run: () => void stepNeedsInput(1), testId: 'notice-cannot-submit-next' }]}
      onDismiss={() => {
        emphasizeNeedsInput(false);
        withdrawNotice(CannotSubmitNoticeId);
      }}
    />
  );
}
