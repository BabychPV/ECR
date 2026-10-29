import { lazy, Suspense, useEffect, useRef, useState, type JSX } from 'react';
import { Badge, Button } from '@mantine/core';
import type { DocumentTableDto } from '@/api/types';
import { t } from '@/shared/i18n';
import { useUrlState } from '@/shared/ui/useUrlState';
import { useDiscussionSummary } from './api';
import {
  consumeChatFocus,
  DiscussionPanelValue,
  publishSummary,
  requestChatFocus,
  setChatTable,
  useChatOpenRequests,
} from './chatStore';
import { useChatTable } from './useChatTable';

/**
 * Entry of the table chat on the document page: the header button ("Discussion" + unread counter, next to Issues /
 * History) and the host of the right panel.
 *
 * ⛔ This module is itself a LAZY chunk of `DocumentPage` (`D-132`), and the panel (messages, composer, mention list)
 * is a second lazy chunk inside it: nothing of the chat is in the route graph, and the panel code loads only when a
 * person opens it. The button only needs the summary (`GET …/discussions/summary`, one indexed query per page load).
 *
 * ⚠ No chat for the user = no button: a failed summary (hidden document, a server that does not know the route) and
 * a read-only user of a document without any discussion show nothing, and the page is not an error.
 */
const TableChatPanel = lazy(async () => ({
  default: (await import('./TableChatPanel')).TableChatPanel,
}));

/** Prewarms the panel chunk on hover/focus of the button. */
function prefetchPanel(): void {
  import('./TableChatPanel').catch(() => undefined);
}

export function TableChatButton({ documentId }: { readonly documentId: number }): JSX.Element | null {
  const summary = useDiscussionSummary(documentId);
  const [panel, setPanel] = useUrlState('panel');
  const open = panel === DiscussionPanelValue;

  // ⚠ A body that is not a summary (a stand without the route answers with something else) is "no chat".
  const data = summary.data;
  const items = data !== undefined && Array.isArray(data.items) ? data.items : undefined;

  // The badges in the table titles read what the button published.
  useEffect(() => {
    publishSummary(documentId, items ?? null);

    return () => publishSummary(documentId, null);
  }, [documentId, items]);

  if (data === undefined || items === undefined) return null;
  if (!data.canWrite && items.length === 0) return null;

  const unread = items.reduce((sum, item) => sum + item.unread, 0);
  const mentioned = items.some((item) => item.mentionedMe);
  const label = unread > 0 ? t('tableChat.buttonUnread', { n: unread }) : t('tableChat.button');

  return (
    <Button
      size="xs"
      variant={open ? 'light' : 'subtle'}
      color={open ? 'brand' : 'gray'}
      {...(open ? {} : { c: 'dimmed' })}
      aria-pressed={open}
      aria-label={label}
      title={mentioned ? t('tableChat.buttonMentioned') : t('tableChat.buttonTitle')}
      leftSection={<ChatGlyph />}
      rightSection={
        unread > 0 ? (
          <Badge size="sm" circle color="brand" data-chat-unread-total={String(unread)} aria-hidden="true">
            {unread}
          </Badge>
        ) : null
      }
      onMouseEnter={prefetchPanel}
      onFocus={prefetchPanel}
      onClick={() => {
        if (open) {
          setPanel(null);
          return;
        }
        // Without an explicit choice the panel follows the focused table.
        setChatTable(null);
        requestChatFocus();
        setPanel(DiscussionPanelValue);
      }}
      data-discussion-trigger=""
    >
      {t('tableChat.button')}
    </Button>
  );
}

export interface TableChatHostProps {
  readonly documentId: number;
  readonly periodKey: number;
  /** Every table the user sees (`GET …/documents/{id}/tables`). */
  readonly tables: readonly DocumentTableDto[];
  /** Tables of the active sheet — the fallback binding. */
  readonly activeTables: readonly DocumentTableDto[];
}

/** Mounts the panel while `?panel=discussion` is in the address. */
export function TableChatHost({ documentId, periodKey, tables, activeTables }: TableChatHostProps): JSX.Element | null {
  const [panel, setPanel] = useUrlState('panel');
  const table = useChatTable(tables, activeTables, periodKey);

  // A badge in a table title asks to open the panel; the address is written here (the grid has no router).
  const requests = useChatOpenRequests();
  const seen = useRef(requests);
  useEffect(() => {
    if (requests === seen.current) return;

    seen.current = requests;
    setPanel(DiscussionPanelValue);
  }, [requests, setPanel]);

  if (panel !== DiscussionPanelValue || table === undefined) return null;

  return (
    <PanelMount
      documentId={documentId}
      periodKey={periodKey}
      table={table}
      onClose={(refocus) => {
        setPanel(null);
        setChatTable(null);
        if (refocus) window.setTimeout(() => document.querySelector<HTMLElement>('[data-discussion-trigger]')?.focus(), 0);
      }}
    />
  );
}

function PanelMount({
  documentId,
  periodKey,
  table,
  onClose,
}: {
  readonly documentId: number;
  readonly periodKey: number;
  readonly table: DocumentTableDto;
  readonly onClose: (refocus: boolean) => void;
}): JSX.Element {
  // Read once, when the panel mounts: a human action asked for it (button/badge), not an address on load.
  const [takeFocus] = useState(consumeChatFocus);

  return (
    <Suspense fallback={null}>
      <TableChatPanel documentId={documentId} periodKey={periodKey} table={table} onClose={onClose} takeFocus={takeFocus} />
    </Suspense>
  );
}

/** Speech bubble; decorative — the button has its own name. */
function ChatGlyph(): JSX.Element {
  return (
    <svg
      width={16}
      height={16}
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth={1.75}
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
      focusable="false"
    >
      <path d="M21 12a8 8 0 0 1-11.6 7.1L4 20l1.1-4.4A8 8 0 1 1 21 12z" />
    </svg>
  );
}
