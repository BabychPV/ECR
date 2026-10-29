import { useQuery, type UseQueryResult } from '@tanstack/react-query';
import { apiFetch, apiFetchIfChanged, EcrApiError, type Conditional } from '@/api/client';
import { queryKeys } from '@/api/queryKeys';
import type {
  DiscussionMessage,
  DiscussionPage,
  DiscussionSummary,
  EditMessageRequest,
  MarkReadRequest,
  MentionCandidate,
  MentionTasks,
  PostMessageRequest,
} from './types';

/*
 * ⛔ Addresses are LITERALS in the call itself (a template string with `${…}`), and the method is a literal
 * `method: '…'` next to it: `EndpointCoverageTests` reads the code with a regular expression and does not see
 * addresses assembled by helpers. The schema conformity of every call (method, path, body) is guarded by
 * `__tests__/api.contract.test.ts`.
 */

/** The server caps `limit` at 100. */
export const PollPageSize = 100;

/** First page and "load older" step. */
export const MessagesPageSize = 50;

/** Poll interval while the panel is open and the tab is visible (plan: 15 s). */
export const DiscussionPollMs = 15_000;

/** Body limit of the server (`TableDiscussionMessage.MaxBodyLength`). */
export const MaxBodyLength = 4000;

/** Shortest mention query (server: `q` ≥ 2). */
export const MinMentionQuery = 2;

/** Candidates the server returns at most. */
export const MaxMentionCandidates = 20;

export interface MessagesQuery {
  readonly before?: number;
  readonly after?: number;
  readonly limit?: number;
  /** `null`/absent — all periods. */
  readonly periodKey?: number | null;
}

function queryString(query: MessagesQuery): string {
  const params = new URLSearchParams();
  if (query.before !== undefined) params.set('before', String(query.before));
  if (query.after !== undefined) params.set('after', String(query.after));
  if (query.limit !== undefined) params.set('limit', String(query.limit));
  if (query.periodKey !== undefined && query.periodKey !== null) params.set('periodKey', String(query.periodKey));

  return params.toString();
}

export function getDiscussionSummary(documentId: number): Promise<DiscussionSummary> {
  return apiFetch<DiscussionSummary>(`/api/v1/documents/${String(documentId)}/discussions/summary`);
}

/**
 * A page of messages. `etag` of the previous answer makes the poll cheap: `null` result is `304` ("nothing changed").
 */
export function getMessages(
  documentId: number,
  tableCode: string,
  query: MessagesQuery,
  etag: string | null,
): Promise<Conditional<DiscussionPage> | null> {
  return apiFetchIfChanged<DiscussionPage>(
    `/api/v1/documents/${String(documentId)}/tables/${encodeURIComponent(tableCode)}/messages?${queryString(query)}`,
    etag,
  );
}

/** `201` for a new message, `200` for a retry of the same `clientMessageId` (the existing message comes back). */
export function postMessage(documentId: number, tableCode: string, request: PostMessageRequest): Promise<DiscussionMessage> {
  return apiFetch<DiscussionMessage>(
    `/api/v1/documents/${String(documentId)}/tables/${encodeURIComponent(tableCode)}/messages`,
    { method: 'POST', body: JSON.stringify(request) },
  );
}

export function editMessage(
  documentId: number,
  tableCode: string,
  messageId: number,
  request: EditMessageRequest,
): Promise<DiscussionMessage> {
  return apiFetch<DiscussionMessage>(
    `/api/v1/documents/${String(documentId)}/tables/${encodeURIComponent(tableCode)}/messages/${String(messageId)}`,
    { method: 'PATCH', body: JSON.stringify(request) },
  );
}

/** Soft delete; a repeat is `204` too. */
export function deleteMessage(documentId: number, tableCode: string, messageId: number): Promise<void> {
  return apiFetch<void>(
    `/api/v1/documents/${String(documentId)}/tables/${encodeURIComponent(tableCode)}/messages/${String(messageId)}`,
    { method: 'DELETE' },
  );
}

/** The server clamps the threshold to the highest id the user can see. */
export function markRead(documentId: number, tableCode: string, lastReadMessageId: number): Promise<void> {
  const body: MarkReadRequest = { lastReadMessageId };

  return apiFetch<void>(`/api/v1/documents/${String(documentId)}/tables/${encodeURIComponent(tableCode)}/read`, {
    method: 'PUT',
    body: JSON.stringify(body),
  });
}

export function getMentionCandidates(
  documentId: number,
  tableCode: string,
  periodKey: number,
  q: string,
): Promise<readonly MentionCandidate[]> {
  return apiFetch<readonly MentionCandidate[]>(
    `/api/v1/documents/${String(documentId)}/tables/${encodeURIComponent(tableCode)}/mention-candidates?q=${encodeURIComponent(q)}&periodKey=${String(periodKey)}`,
  );
}

export function getMentionTasks(limit: number): Promise<MentionTasks> {
  return apiFetch<MentionTasks>(`/api/v1/me/table-discussion-mentions?limit=${String(limit)}`);
}

/** How many mentions My tasks asks for. */
export const MentionTasksLimit = 20;

/**
 * Counters of every table of the document that has messages. ⚠ A failure is NOT a page error: the chat is an
 * accessory of the document, so the button and the badges simply do not appear (hidden document/table = `404`,
 * a server that does not know the route = `404`/`500`).
 */
export function useDiscussionSummary(documentId: number, enabled = true): UseQueryResult<DiscussionSummary> {
  return useQuery({
    queryKey: queryKeys.tableChat.summary(documentId),
    queryFn: () => getDiscussionSummary(documentId),
    enabled,
    retry: false,
    staleTime: 30_000,
  });
}

/** Unread mentions of the current user (the "mentions" block of My tasks). */
export function useMentionTasks(opened: boolean): UseQueryResult<MentionTasks> {
  return useQuery({
    queryKey: queryKeys.tableChat.mentions(),
    queryFn: () => getMentionTasks(MentionTasksLimit),
    enabled: opened,
    retry: false,
    staleTime: 0,
  });
}

/** Candidates for `@`: people who can see the table; the query is idle below `MinMentionQuery` characters. */
export function useMentionCandidates(
  documentId: number,
  tableCode: string,
  periodKey: number,
  query: string,
): UseQueryResult<readonly MentionCandidate[]> {
  const trimmed = query.trim();

  return useQuery({
    queryKey: queryKeys.tableChat.candidates(documentId, tableCode, periodKey, trimmed),
    queryFn: () => getMentionCandidates(documentId, tableCode, periodKey, trimmed),
    enabled: trimmed.length >= MinMentionQuery,
    retry: false,
    staleTime: 60_000,
  });
}

/** `404`/`403` of a chat route: there is no chat for this user (hidden table/document). */
export function isChatUnavailable(error: unknown): boolean {
  return error instanceof EcrApiError && (error.problem.status === 404 || error.problem.status === 403);
}
