import type { JSX, ReactNode } from 'react';
import { t } from '@/shared/i18n';
import { NoticeRow, type NoticeAction } from './NoticeRow';
import { useNoticeHost } from './noticeStore';
import { useOpenStatus } from './useOpenStatus';

interface NoticeSlotProps {
  readonly id: string;
  readonly priority: number;
  /** Зупиняє наступну дію людини — у рядку повідомлень завжди видиме. */
  readonly blocking?: boolean;
  readonly tone: 'error' | 'warning' | 'info';
  /** Підсумок ОДНИМ рядком (рядок над сіткою). */
  readonly text: string;
  /** Головні дії підсумку; «Details» (повний вміст у Status) додається сам. */
  readonly actions?: readonly NoticeAction[];
  readonly onDismiss?: () => void;
  readonly testId?: string;
  /** Додаткові `data-*` рядка підсумку. */
  readonly data?: Readonly<Record<`data-${string}`, string>>;
  /** Повний вміст — як і доти (банер, перелік, кнопки): у вкладці Status або на місці, коли рядка немає. */
  readonly children: ReactNode;
}

/**
 * ✎ DOCUX L7: обгортка блоку-повідомлення. На сторінці документа (є `NoticeLine`) блок не займає місця над сіткою: у
 * рядку — підсумок із головною дією й «Details», повний вміст — у вкладці Status. Деінде (інший екран, одиничний тест) —
 * вміст на місці, як і раніше.
 */
export function NoticeSlot({ id, priority, blocking, tone, text, actions = [], onDismiss, testId, data, children }: NoticeSlotProps): JSX.Element | null {
  const openStatus = useOpenStatus();
  const hosted = useNoticeHost(id, {
    priority,
    ...(blocking === true ? { blocking: true } : {}),
    render: () => (
      <NoticeRow
        tone={tone}
        text={text}
        actions={[...actions, { label: t('document.notices.details'), run: openStatus }]}
        {...(onDismiss === undefined ? {} : { onDismiss })}
        {...(testId === undefined ? {} : { testId })}
        {...(data === undefined ? {} : { data })}
      />
    ),
    renderFull: () => <>{children}</>,
  });

  return hosted ? null : <>{children}</>;
}
