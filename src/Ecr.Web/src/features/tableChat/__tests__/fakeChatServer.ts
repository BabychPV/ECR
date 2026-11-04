import { vi } from 'vitest';
import type { DiscussionMessage } from '../types';

/**
 * A tiny in-memory server of the table chat for component tests: the routes of `TableDiscussionController` with the
 * semantics the client relies on (keyset pagination, `after` poll with ETag/304, period filter, idempotent POST).
 */
export interface RecordedRequest {
  readonly method: string;
  readonly url: string;
  readonly path: string;
  readonly params: URLSearchParams;
  readonly headers: Record<string, string>;
  readonly body: Record<string, unknown> | undefined;
}

export function message(id: number, over: Partial<DiscussionMessage> = {}): DiscussionMessage {
  return {
    id,
    tableCode: 'T1',
    periodKey: 202606,
    authorUserId: 2,
    authorName: 'Aidar Nurlanov',
    body: `message ${String(id)}`,
    isDeleted: false,
    createdAt: '2026-06-01T10:00:00Z',
    editedAt: null,
    deletedAt: null,
    canEdit: false,
    editableUntil: null,
    canDelete: false,
    mentions: [],
    ...over,
  };
}

export class FakeChatServer {
  messages: DiscussionMessage[] = [];
  revision = 1;
  lastRead = 0;
  canWrite = true;
  canModerate = false;
  candidates: { userId: number; name: string }[] = [];
  failNextPost = false;
  readonly requests: RecordedRequest[] = [];
  private readonly byClientId = new Map<string, DiscussionMessage>();

  /** A change by somebody else (edit/delete/new message): moves the revision like the real server. */
  touch(): void {
    this.revision += 1;
  }

  page(messages: DiscussionMessage[], hasMoreBefore: boolean): Record<string, unknown> {
    return {
      messages,
      hasMoreBefore,
      revision: this.revision,
      lastMessageId: this.messages.at(-1)?.id ?? 0,
      lastReadMessageId: this.lastRead,
      canWrite: this.canWrite,
      canModerate: this.canModerate,
    };
  }

  install(): void {
    vi.stubGlobal(
      'fetch',
      vi.fn((input: RequestInfo | URL, init?: RequestInit) => Promise.resolve(this.handle(input, init))),
    );
  }

  of(method: string, pathPart: string): RecordedRequest[] {
    return this.requests.filter((request) => request.method === method && request.path.includes(pathPart));
  }

  private handle(input: RequestInfo | URL, init?: RequestInit): Response {
    const url = String(input);
    const parsed = new URL(url, 'http://localhost');
    const method = (init?.method ?? 'GET').toUpperCase();
    const headers = Object.fromEntries(new Headers(init?.headers).entries());
    const body = init?.body === undefined ? undefined : (JSON.parse(String(init.body)) as Record<string, unknown>);
    this.requests.push({ method, url, path: parsed.pathname, params: parsed.searchParams, headers, body });

    const path = parsed.pathname;

    if (method === 'GET' && path.endsWith('/messages')) return this.list(parsed.searchParams, headers);
    if (method === 'POST' && path.endsWith('/messages')) return this.post(body ?? {});
    if (method === 'PATCH' && /\/messages\/\d+$/.test(path)) return this.patch(path, body ?? {});
    if (method === 'DELETE' && /\/messages\/\d+$/.test(path)) return this.remove(path);
    if (method === 'PUT' && path.endsWith('/read')) {
      this.lastRead = Math.max(this.lastRead, Number(body?.['lastReadMessageId'] ?? 0));
      return new Response(null, { status: 204 });
    }
    if (method === 'GET' && path.endsWith('/mention-candidates')) {
      const q = (parsed.searchParams.get('q') ?? '').toLowerCase();
      return json(this.candidates.filter((candidate) => candidate.name.toLowerCase().includes(q)));
    }

    return new Response('{}', { status: 404, headers: { 'Content-Type': 'application/problem+json' } });
  }

  private list(params: URLSearchParams, headers: Record<string, string>): Response {
    const etag = `"${String(this.revision)}"`;
    if (headers['if-none-match'] === etag) return new Response(null, { status: 304 });

    const limit = Number(params.get('limit') ?? 50);
    const period = params.get('periodKey');
    const all = this.messages.filter((m) => period === null || String(m.periodKey) === period);

    let slice: DiscussionMessage[];
    let hasMoreBefore: boolean;
    if (params.has('after')) {
      slice = all.filter((m) => m.id > Number(params.get('after'))).slice(0, limit);
      hasMoreBefore = false;
    } else if (params.has('before')) {
      const older = all.filter((m) => m.id < Number(params.get('before')));
      slice = older.slice(-limit);
      hasMoreBefore = older.length > limit;
    } else {
      slice = all.slice(-limit);
      hasMoreBefore = all.length > limit;
    }

    return json(this.page(slice, hasMoreBefore), 200, { ETag: etag });
  }

  private post(body: Record<string, unknown>): Response {
    if (this.failNextPost) {
      this.failNextPost = false;
      return new Response(JSON.stringify({ title: 'boom', status: 500 }), {
        status: 500,
        headers: { 'Content-Type': 'application/problem+json' },
      });
    }

    const clientId = typeof body['clientMessageId'] === 'string' ? body['clientMessageId'] : null;
    const existing = clientId === null ? undefined : this.byClientId.get(clientId);
    if (existing !== undefined) return json(existing, 200);

    const created = message((this.messages.at(-1)?.id ?? 0) + 1, {
      authorUserId: 1,
      authorName: 'Me',
      body: String(body['body']),
      periodKey: Number(body['periodKey']),
      canEdit: true,
      editableUntil: new Date(Date.now() + 15 * 60_000).toISOString(),
      canDelete: true,
    });
    this.messages.push(created);
    this.revision += 1;
    if (clientId !== null) this.byClientId.set(clientId, created);

    return json(created, 201);
  }

  private patch(path: string, body: Record<string, unknown>): Response {
    const id = Number(path.split('/').at(-1));
    const index = this.messages.findIndex((m) => m.id === id);
    const current = this.messages[index];
    if (current === undefined) return new Response('{}', { status: 404 });

    const next = { ...current, body: String(body['body']), editedAt: '2026-06-01T10:05:00Z' };
    this.messages[index] = next;
    this.revision += 1;

    return json(next, 200);
  }

  private remove(path: string): Response {
    const id = Number(path.split('/').at(-1));
    const index = this.messages.findIndex((m) => m.id === id);
    const current = this.messages[index];
    if (current !== undefined) {
      this.messages[index] = { ...current, body: null, isDeleted: true, deletedAt: '2026-06-01T10:06:00Z', canEdit: false, canDelete: false };
      this.revision += 1;
    }

    return new Response(null, { status: 204 });
  }
}

function json(body: unknown, status = 200, headers: Record<string, string> = {}): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json', ...headers } });
}
