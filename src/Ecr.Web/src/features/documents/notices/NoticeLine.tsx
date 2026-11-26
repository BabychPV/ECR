import type { JSX } from 'react';
import { Button, Text } from '@mantine/core';
import { t } from '@/shared/i18n';
import { useUrlState } from '@/shared/ui/useUrlState';
import { useNotices } from './noticeStore';
import './notices.css';

/** Параметр адреси інспектора (`DocumentInspector`: `?panel=issues|history|info|status`). */
const PanelParam = 'panel';

/**
 * ✎ DOCUX L7 (§3.3 плану): над сіткою — ЩОНАЙБІЛЬШЕ один рядок: найважливіше повідомлення і «+N more», що відкриває
 * вкладку Status інспектора з повним переліком (кожен пункт — зі своєю дією). Повідомлень немає — рядка немає.
 */
export function NoticeLine(): JSX.Element | null {
  const notices = useNotices();
  const [, setPanel] = useUrlState(PanelParam);
  const top = notices[0];
  if (top === undefined) return null;
  const more = notices.length - 1;

  return (
    <section className="ecr-notice-line" aria-label={t('document.notices.lineLabel')} data-testid="document-notice-line" data-notice-top={top.id}>
      <div className="ecr-notice-top">{top.render()}</div>
      {more > 0 && (
        <Button size="xs" variant="default" onClick={() => setPanel('status')} data-testid="document-notice-more">
          {t('document.notices.more', { count: more })}
        </Button>
      )}
    </section>
  );
}

/** ✎ DOCUX L7: вкладка Status інспектора — усі повідомлення за пріоритетом; порожньо — пояснення. */
export function NoticeList(): JSX.Element {
  const notices = useNotices();
  if (notices.length === 0) {
    return (
      <Text size="sm" c="dimmed" p="sm" data-testid="document-notice-list-empty">
        {t('document.notices.none')}
      </Text>
    );
  }

  return (
    <div className="ecr-notice-list" data-testid="document-notice-list">
      {notices.map((notice) => (
        <div key={notice.id} data-notice-id={notice.id}>
          {notice.render()}
        </div>
      ))}
    </div>
  );
}

/** Скільки повідомлень (лічильник на вкладці Status). */
export function useNoticeCount(): number {
  return useNotices().length;
}
