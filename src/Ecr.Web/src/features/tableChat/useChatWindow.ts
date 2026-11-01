import { useCallback, useEffect, useRef, useState } from 'react';
import { usePollWhileVisible } from '@/shared/ui/usePollWhileVisible';
import { DiscussionPollMs, MessagesPageSize, PollPageSize, getMessages } from './api';
import { mergeMessages } from './chatModel';
import type { DiscussionMessage, DiscussionPage } from './types';

/**
 * The loaded window of one table chat: the latest page, older pages on demand, a cheap poll.
 *
 * ⛔ Not `useQuery`/`useInfiniteQuery`: the window is a SET of pages that is extended at both ends and refetched as
 * a whole when the chat `revision` changes, and the poll is conditional (`If-None-Match` → `304`). A query cache
 * keyed by page would either refetch every page on each tick or show stale edits.
 *
 * Rules (contract of `GET …/messages`):
 *  - poll with `after = lastMessageId` and the previous ETag; `304` — nothing changed;
 *  - an edit or a delete of an OLD message does not move `lastMessageId`, but it moves `revision`: when the poll
 *    answers `200` with a different `revision`, the loaded window is refetched from its first message;
 *  - the poll runs only while the panel is mounted (the host unmounts it on close) and the tab is visible.
 */
export interface ChatWindowState {
  readonly status: 'loading' | 'ready' | 'error';
  readonly error: unknown;
  readonly messages: readonly DiscussionMessage[];
  readonly hasMoreBefore: boolean;
  readonly revision: number;
  readonly lastMessageId: number;
  readonly lastReadMessageId: number;
  readonly canWrite: boolean;
  readonly canModerate: boolean;
  /** Read mark when the window was first loaded: "new" markers stay while the user reads the messages. */
  readonly readBaseline: number;
  readonly loadingOlder: boolean;
}

const Initial: ChatWindowState = {
  status: 'loading',
  error: null,
  messages: [],
  hasMoreBefore: false,
  revision: 0,
  lastMessageId: 0,
  lastReadMessageId: 0,
  canWrite: false,
  canModerate: false,
  readBaseline: 0,
  loadingOlder: false,
};

/** A reload walks at most this many pages of `PollPageSize` (2000 messages) — a guard, not a normal case. */
const MaxReloadRounds = 20;

function readyFrom(page: DiscussionPage, previous: ChatWindowState | null): ChatWindowState {
  return {
    status: 'ready',
    error: null,
    messages: page.messages,
    hasMoreBefore: page.hasMoreBefore,
    revision: page.revision,
    lastMessageId: page.lastMessageId,
    lastReadMessageId: page.lastReadMessageId,
    canWrite: page.canWrite,
    canModerate: page.canModerate,
    readBaseline: previous === null ? page.lastReadMessageId : previous.readBaseline,
    loadingOlder: false,
  };
}

export interface ChatWindow {
  readonly state: ChatWindowState;
  /** Older page; rejects with the server error (the caller shows it). */
  readonly loadOlder: () => Promise<void>;
  /** Refetch the loaded window now (after an own write, "retry"). */
  readonly reload: () => Promise<void>;
  /** Put a message returned by the server (own post/edit) into the window without waiting for the poll. */
  readonly upsert: (message: DiscussionMessage) => void;
  /** The user has seen everything up to `messageId`. */
  readonly markReadLocally: (messageId: number) => void;
  /** Fetch the first page again after a failed load. */
  readonly retry: () => void;
}

export function useChatWindow(
  documentId: number,
  tableCode: string,
  periodFilter: number | null,
): ChatWindow {
  const [state, setState] = useState<ChatWindowState>(Initial);
  const stateRef = useRef(state);
  stateRef.current = state;

  const etagRef = useRef<string | null>(null);
  /** Bumped on every key change/unmount: answers that arrive for an older key are dropped. */
  const generation = useRef(0);
  const busy = useRef(false);
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    generation.current += 1;
    const mine = generation.current;
    etagRef.current = null;
    busy.current = false;
    setState(Initial);

    void (async () => {
      try {
        const result = await getMessages(documentId, tableCode, { limit: MessagesPageSize, periodKey: periodFilter }, null);
        if (mine !== generation.current || result === null) return;

        etagRef.current = result.etag;
        setState(readyFrom(result.body, null));
      } catch (error) {
        if (mine !== generation.current) return;
        setState({ ...Initial, status: 'error', error });
      }
    })();

    return () => {
      generation.current += 1;
    };
  }, [documentId, tableCode, periodFilter, attempt]);

  /** Refetch the loaded window from its first message (or the latest page when nothing is loaded). */
  const reloadWindow = useCallback(
    async (mine: number): Promise<void> => {
      const current = stateRef.current;
      const first = current.messages[0]?.id;
      const pages: DiscussionPage[] = [];

      if (first === undefined) {
        const result = await getMessages(documentId, tableCode, { limit: MessagesPageSize, periodKey: periodFilter }, null);
        if (mine !== generation.current || result === null) return;
        pages.push(result.body);
      } else {
        let after = first - 1;
        for (let round = 0; round < MaxReloadRounds; round += 1) {
          const result = await getMessages(documentId, tableCode, { after, limit: PollPageSize, periodKey: periodFilter }, null);
          if (mine !== generation.current || result === null) return;
          pages.push(result.body);

          const last = result.body.messages.at(-1);
          if (result.body.messages.length < PollPageSize || last === undefined) break;
          after = last.id;
        }
      }

      const tail = pages.at(-1);
      if (tail === undefined) return;

      const messages = pages.flatMap((page) => page.messages);
      setState((previous) => ({
        ...readyFrom(tail, previous),
        messages: first === undefined ? messages : mergeFrom(previous.messages, messages, first),
        // Older pages already loaded stay as they were: only the region from `first` was refetched.
        hasMoreBefore: first === undefined ? tail.hasMoreBefore : previous.hasMoreBefore,
        loadingOlder: previous.loadingOlder,
      }));
    },
    [documentId, tableCode, periodFilter],
  );

  const poll = useCallback(async (): Promise<void> => {
    const current = stateRef.current;
    if (current.status !== 'ready' || busy.current) return;

    busy.current = true;
    const mine = generation.current;

    try {
      const result = await getMessages(
        documentId,
        tableCode,
        { after: current.lastMessageId, limit: PollPageSize, periodKey: periodFilter },
        etagRef.current,
      );
      if (mine !== generation.current || result === null) return;

      etagRef.current = result.etag;
      if (result.body.revision !== stateRef.current.revision) await reloadWindow(mine);
    } catch {
      // A failed poll is silent: the next tick retries, and the window on screen is still valid.
    } finally {
      busy.current = false;
    }
  }, [documentId, tableCode, periodFilter, reloadWindow]);

  usePollWhileVisible(poll, DiscussionPollMs);

  const loadOlder = useCallback(async (): Promise<void> => {
    const current = stateRef.current;
    const first = current.messages[0]?.id;
    if (first === undefined || !current.hasMoreBefore || current.loadingOlder) return;

    const mine = generation.current;
    setState((previous) => ({ ...previous, loadingOlder: true }));

    try {
      const result = await getMessages(documentId, tableCode, { before: first, limit: MessagesPageSize, periodKey: periodFilter }, null);
      if (mine !== generation.current || result === null) return;

      setState((previous) => ({
        ...previous,
        messages: mergeMessages(previous.messages, result.body.messages),
        hasMoreBefore: result.body.hasMoreBefore,
        loadingOlder: false,
      }));
    } catch (error) {
      if (mine === generation.current) setState((previous) => ({ ...previous, loadingOlder: false }));
      throw error;
    }
  }, [documentId, tableCode, periodFilter]);

  const reload = useCallback(async (): Promise<void> => {
    try {
      await reloadWindow(generation.current);
    } catch {
      // The poll picks the change up on the next tick.
    }
  }, [reloadWindow]);

  const upsert = useCallback((message: DiscussionMessage): void => {
    setState((previous) => ({
      ...previous,
      messages: mergeMessages(previous.messages, [message]),
      lastMessageId: Math.max(previous.lastMessageId, message.id),
    }));
  }, []);

  const markReadLocally = useCallback((messageId: number): void => {
    setState((previous) =>
      messageId > previous.lastReadMessageId ? { ...previous, lastReadMessageId: messageId } : previous,
    );
  }, []);

  const retry = useCallback((): void => setAttempt((value) => value + 1), []);

  return { state, loadOlder, reload, upsert, markReadLocally, retry };
}

/** The region from `first` upwards is replaced by the refetched messages; older loaded messages are kept. */
function mergeFrom(
  previous: readonly DiscussionMessage[],
  refetched: readonly DiscussionMessage[],
  first: number,
): DiscussionMessage[] {
  return mergeMessages(
    previous.filter((message) => message.id < first),
    refetched,
  );
}
