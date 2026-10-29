import { useEffect, useLayoutEffect, useMemo, useRef, useState, type JSX } from 'react';
import { ActionIcon, Button, Loader, Text } from '@mantine/core';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { queryKeys } from '@/api/queryKeys';
import type { DocumentTableDto } from '@/api/types';
import { formatPeriodKey } from '@/shared/format/periodKey';
import { t } from '@/shared/i18n';
import { localized } from '@/shared/i18n/localized';
import { useSession } from '@/shared/session/useSession';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { showApiError } from '@/shared/ui/notify';
import { deleteMessage, editMessage, markRead, postMessage } from './api';
import { ChatComposer } from './ChatComposer';
import { ChatMessageItem } from './ChatMessageItem';
import { isUnread } from './chatModel';
import type { DiscussionMessage, PostMessageRequest } from './types';
import { useChatWindow } from './useChatWindow';
import './tableChat.css';

export interface TableChatPanelProps {
  readonly documentId: number;
  /** The document period: the period a new message is written in and the "this period only" filter. */
  readonly periodKey: number;
  readonly table: DocumentTableDto;
  /** Esc or the close button; `refocus` — return the focus to the button that opened the panel. */
  readonly onClose: (refocus: boolean) => void;
  /** Opened by a human action (the button/badge), not from the address on load: take the focus. */
  readonly takeFocus: boolean;
}

/** The edit window is checked against the wall clock; the panel re-renders it this often. */
const ClockTickMs = 20_000;

/** The list sticks to the bottom while the user is within this many pixels of it. */
const StickThresholdPx = 48;

/** The user must have the panel open this long before messages count as seen. */
const MarkReadDelayMs = 800;

/**
 * Table chat panel (TC): the right side column bound to ONE table of the document.
 *
 * Mockup: the canvas "Table discussion" (design lane, human OK 29.10) over the hybrid kit (`docs/design/hybrid`,
 * `KIT.md` §4 `.aside`, D15-01). Behaviour: `.sync-local/plans/TABLE-CHAT-PLAN.md`.
 *
 * ⛔ Non-modal `aside` (like the inspector, not a `Drawer`): the grid behind stays alive. The window polls only
 * while this component is mounted — the host unmounts it on close — and not while the tab is hidden.
 */
export function TableChatPanel({ documentId, periodKey, table, onClose, takeFocus }: TableChatPanelProps): JSX.Element {
  const session = useSession();
  const me = session.data?.userId;
  const queryClient = useQueryClient();

  const [filter, setFilter] = useState<'all' | 'period'>('all');
  const chat = useChatWindow(documentId, table.tableCode, filter === 'period' ? periodKey : null);
  const { state } = chat;

  const periodText = formatPeriodKey(periodKey);
  const tableName = localized(table.tableNameL10n);

  const heading = useRef<HTMLHeadingElement>(null);
  const scroller = useRef<HTMLDivElement>(null);
  const stick = useRef(true);
  const olderAnchor = useRef<number | null>(null);

  // The page body makes room for the column (CSS `tableChat.css`), as the inspector does.
  useEffect(() => {
    document.body.setAttribute('data-discussion-open', '');

    return () => {
      document.body.removeAttribute('data-discussion-open');
    };
  }, []);

  const [nowMs, setNowMs] = useState(() => Date.now());
  useEffect(() => {
    const timer = window.setInterval(() => setNowMs(Date.now()), ClockTickMs);

    return () => window.clearInterval(timer);
  }, []);

  useEffect(() => {
    if (takeFocus && state.status === 'ready' && !state.canWrite) heading.current?.focus();
  }, [takeFocus, state.status, state.canWrite]);

  const refreshSummary = (): void => {
    void queryClient.invalidateQueries({ queryKey: queryKeys.tableChat.summary(documentId) });
  };

  const post = useMutation({
    meta: { handled: true },
    mutationFn: (request: PostMessageRequest) => postMessage(documentId, table.tableCode, request),
    onSuccess: (message) => {
      stick.current = true;
      chat.upsert(message);
      void chat.reload();
      refreshSummary();
    },
    onError: showApiError,
  });

  const edit = useMutation({
    meta: { handled: true },
    mutationFn: (variables: { message: DiscussionMessage; body: string; mentionUserIds: number[] }) =>
      editMessage(documentId, table.tableCode, variables.message.id, {
        body: variables.body,
        mentionUserIds: variables.mentionUserIds,
      }),
    onSuccess: (message) => {
      chat.upsert(message);
      void chat.reload();
    },
    onError: showApiError,
  });

  const remove = useMutation({
    meta: { handled: true },
    mutationFn: (message: DiscussionMessage) => deleteMessage(documentId, table.tableCode, message.id),
    onSuccess: () => {
      void chat.reload();
      refreshSummary();
    },
    onError: showApiError,
  });

  // Everything up to the newest message is seen once the panel has been open on it for a moment.
  const markedUpTo = useRef(0);
  useEffect(() => {
    if (state.status !== 'ready') return undefined;
    if (state.lastMessageId <= state.lastReadMessageId || state.lastMessageId <= markedUpTo.current) return undefined;
    if (document.visibilityState !== 'visible') return undefined;

    const target = state.lastMessageId;
    const timer = window.setTimeout(() => {
      markedUpTo.current = target;
      markRead(documentId, table.tableCode, target)
        .then(() => {
          chat.markReadLocally(target);
          refreshSummary();
        })
        .catch(() => {
          // Not critical: the next change of the chat retries.
          markedUpTo.current = 0;
        });
    }, MarkReadDelayMs);

    return () => window.clearTimeout(timer);
    // `chat` and `refreshSummary` are fresh each render on purpose: the trigger is the ids, not the callbacks.
  }, [state.status, state.lastMessageId, state.lastReadMessageId, documentId, table.tableCode]);

  // A new table or a new filter starts at the bottom.
  useEffect(() => {
    stick.current = true;
    markedUpTo.current = 0;
  }, [table.tableCode, filter]);

  const firstId = state.messages[0]?.id;
  const lastId = state.messages.at(-1)?.id;

  useLayoutEffect(() => {
    const node = scroller.current;
    if (node === null) return;

    if (olderAnchor.current !== null) {
      // Older messages were put above: keep what the user was reading where it was.
      node.scrollTop += node.scrollHeight - olderAnchor.current;
      olderAnchor.current = null;
      return;
    }

    if (stick.current) node.scrollTop = node.scrollHeight;
  }, [firstId, lastId, state.status]);

  // The read mark of the FIRST load of this table: "new" marks survive a filter switch and the mark-read of the poll.
  const baselineRef = useRef<{ readonly table: string; readonly value: number } | null>(null);
  if (state.status === 'ready' && baselineRef.current?.table !== table.tableCode) {
    baselineRef.current = { table: table.tableCode, value: state.readBaseline };
  }
  const readBaseline = baselineRef.current?.table === table.tableCode ? baselineRef.current.value : state.readBaseline;

  const unreadCount = useMemo(
    () => state.messages.filter((message) => isUnread(message, readBaseline, me)).length,
    [state.messages, readBaseline, me],
  );

  const loadOlder = (): void => {
    olderAnchor.current = scroller.current?.scrollHeight ?? null;
    chat.loadOlder().catch((error: unknown) => {
      olderAnchor.current = null;
      showApiError(error);
    });
  };

  const submit = async (request: PostMessageRequest): Promise<void> => {
    if (post.isPending) return;
    await post.mutateAsync(request);
  };

  const save = async (message: DiscussionMessage, body: string, mentionUserIds: number[]): Promise<void> => {
    await edit.mutateAsync({ message, body, mentionUserIds });
  };

  return (
    <aside
      className="ecr-chat"
      aria-label={t('tableChat.panelTitle')}
      data-chat-panel=""
      data-chat-table={table.tableCode}
      onKeyDown={(event) => {
        if (event.key === 'Escape') {
          event.stopPropagation();
          onClose(true);
        }
      }}
    >
      <div className="ecr-chat-head">
        <h2 ref={heading} tabIndex={-1}>
          {t('tableChat.panelTitle')}
          <Text span size="sm" c="dimmed" fw={400} className="ecr-chat-table" title={tableName}>
            {' · '}
            {tableName}
          </Text>
        </h2>
        <ActionIcon
          size="sm"
          variant="subtle"
          color="gray"
          aria-label={t('tableChat.close')}
          title={t('tableChat.close')}
          onClick={() => onClose(true)}
          data-chat-close=""
        >
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
            <path d="M6 6l12 12M18 6L6 18" />
          </svg>
        </ActionIcon>
      </div>

      <div className="ecr-chat-filter" role="group" aria-label={t('tableChat.periodFilter')}>
        <Button
          size="xs"
          variant={filter === 'all' ? 'light' : 'default'}
          aria-pressed={filter === 'all'}
          onClick={() => setFilter('all')}
          data-chat-filter="all"
        >
          {t('tableChat.allPeriods')}
        </Button>
        <Button
          size="xs"
          variant={filter === 'period' ? 'light' : 'default'}
          aria-pressed={filter === 'period'}
          onClick={() => setFilter('period')}
          data-chat-filter="period"
        >
          {t('tableChat.thisPeriod', { period: periodText })}
        </Button>
      </div>

      <div
        ref={scroller}
        className="ecr-chat-scroll"
        role="log"
        aria-live="off"
        aria-label={t('tableChat.messages')}
        tabIndex={0}
        onScroll={(event) => {
          const node = event.currentTarget;
          stick.current = node.scrollHeight - node.scrollTop - node.clientHeight < StickThresholdPx;
        }}
      >
        {state.status === 'loading' && (
          <div className="ecr-chat-state" role="status">
            <Loader size="xs" />
            <Text size="sm" c="dimmed">
              {t('tableChat.loading')}
            </Text>
          </div>
        )}

        {state.status === 'error' && (
          <div className="ecr-chat-state">
            <ErrorAlert error={state.error} onRetry={chat.retry} />
          </div>
        )}

        {state.status === 'ready' && state.messages.length === 0 && (
          <div className="ecr-chat-state" data-chat-empty="">
            <b>{filter === 'period' ? t('tableChat.emptyPeriod') : t('tableChat.empty')}</b>
            <Text size="sm" c="dimmed">
              {filter === 'period' ? t('tableChat.emptyPeriodHint') : t('tableChat.emptyHint')}
            </Text>
          </div>
        )}

        {state.status === 'ready' && state.hasMoreBefore && (
          <div className="ecr-chat-older">
            <Button size="xs" variant="default" loading={state.loadingOlder} onClick={loadOlder} data-chat-older="">
              {t('tableChat.loadOlder')}
            </Button>
          </div>
        )}

        {state.status === 'ready' && state.messages.length > 0 && (
          <ol className="ecr-chat-list" data-chat-unread-count={unreadCount}>
            {state.messages.map((message) => (
              <ChatMessageItem
                key={message.id}
                message={message}
                nowMs={nowMs}
                unread={isUnread(message, readBaseline, me)}
                onSave={save}
                onRemove={async (target) => {
                  await remove.mutateAsync(target);
                }}
              />
            ))}
          </ol>
        )}
      </div>

      <div className="ecr-chat-foot">
        {state.status === 'ready' && state.canWrite && (
          <ChatComposer
            documentId={documentId}
            tableCode={table.tableCode}
            periodKey={periodKey}
            periodText={periodText}
            onSubmit={submit}
            pending={post.isPending}
            autoFocus={takeFocus}
          />
        )}

        {state.status === 'ready' && !state.canWrite && (
          <Text size="sm" c="dimmed" data-chat-readonly="">
            {t('tableChat.readOnly')}
          </Text>
        )}
      </div>
    </aside>
  );
}
