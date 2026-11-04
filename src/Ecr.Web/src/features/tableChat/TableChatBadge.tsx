import type { JSX } from 'react';
import { t } from '@/shared/i18n';
import { chatKey } from './chatKey';
import { requestChatFocus, requestChatOpen, setChatTable, useTableSummaryItem } from './chatStore';
import './tableChatBadge.css';

/**
 * Badge in a table title: how many messages the table chat has and how many are new; a click opens the panel on
 * THIS table (mockup: "12 · 3 new" next to the title).
 *
 * ⛔ Lives in the grid chunk (`SheetTables`), so it imports only the tiny store and CSS: no `QueryClient`, no router,
 * no panel. The counters are what the header button published; a table without messages (or a chat that is
 * unavailable to the user) has no badge at all — the header button opens the panel for an empty table. The click
 * only ASKS to open (`requestChatOpen`); the host writes `?panel=discussion`.
 */
export function TableChatBadge({
  documentId,
  tableCode,
  sheetCopyId = null,
}: {
  readonly documentId: number;
  readonly tableCode: string;
  /** TC02: the copy of the sheet the table belongs to; `null` — the original (its counters are NOT the copy's). */
  readonly sheetCopyId?: number | null;
}): JSX.Element | null {
  const key = chatKey(tableCode, sheetCopyId);
  const item = useTableSummaryItem(documentId, tableCode, sheetCopyId);

  if (item === undefined || item.total === 0) return null;

  const label = t('tableChat.badgeLabel', { total: item.total, unread: item.unread });

  return (
    <button
      type="button"
      className="ecr-chat-badge"
      data-unread={item.unread > 0 ? '' : undefined}
      data-chat-badge={key}
      aria-label={label}
      title={label}
      onClick={() => {
        setChatTable(key);
        requestChatFocus();
        requestChatOpen();
      }}
    >
      <svg width={12} height={12} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2} strokeLinecap="round" strokeLinejoin="round" aria-hidden="true" focusable="false">
        <path d="M21 12a8 8 0 0 1-11.6 7.1L4 20l1.1-4.4A8 8 0 1 1 21 12z" />
      </svg>
      <span>{item.unread > 0 ? t('tableChat.badgeNew', { total: item.total, unread: item.unread }) : item.total}</span>
    </button>
  );
}
