import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  deleteMessage,
  editMessage,
  getDiscussionSummary,
  getMentionCandidates,
  getMentionTasks,
  getMessages,
  markRead,
  postMessage,
} from '../api';

/**
 * `api.ts` ↔ `contracts/openapi.snapshot.json`: every call is an existing "method + path" pair of the contract, the
 * bodies carry all required fields and nothing the server does not know, and EVERY table-chat action of the server
 * has a call in the client (the same guard as for edit assignments).
 */

interface Schema {
  readonly $ref?: string;
  readonly required?: readonly string[];
  readonly properties?: Record<string, Schema>;
}

interface Operation {
  readonly requestBody?: { readonly content: Record<string, { readonly schema: Schema }> };
}

const spec = JSON.parse(readFileSync(resolve(process.cwd(), '../../contracts/openapi.snapshot.json'), 'utf8')) as {
  paths: Record<string, Record<string, Operation>>;
  components: { schemas: Record<string, Schema> };
};

const deref = (schema: Schema): Schema =>
  schema.$ref === undefined ? schema : (spec.components.schemas[schema.$ref.split('/').pop() ?? ''] ?? {});

interface Call {
  readonly method: string;
  readonly path: string;
  readonly body: unknown;
  readonly headers: Record<string, string>;
}

const calls: Call[] = [];

function stubFetch(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
      calls.push({
        method: (init?.method ?? 'GET').toUpperCase(),
        path: String(input).split('?')[0] ?? '',
        body: init?.body === undefined ? undefined : JSON.parse(String(init.body)),
        headers: Object.fromEntries(new Headers(init?.headers).entries()),
      });

      return Promise.resolve(new Response('{}', { status: 200, headers: { 'Content-Type': 'application/json' } }));
    }),
  );
}

function templateOf(path: string): string | undefined {
  return Object.keys(spec.paths).find((template) =>
    new RegExp(`^${template.replace(/\{[^}]+\}/g, '[^/]+')}$`).test(path),
  );
}

function violations(value: unknown, schema: Schema, at: string): string[] {
  const resolved = deref(schema);
  if (value === null || typeof value !== 'object' || Array.isArray(value) || resolved.properties === undefined) return [];

  const record = value as Record<string, unknown>;
  const known = resolved.properties;
  const out: string[] = [];

  for (const key of resolved.required ?? []) if (!(key in record)) out.push(`${at}.${key}: required field not sent`);
  for (const key of Object.keys(record)) {
    const child = known[key];
    if (child === undefined) out.push(`${at}.${key}: the server has no such field`);
    else out.push(...violations(record[key], child, `${at}.${key}`));
  }

  return out;
}

async function callEverything(): Promise<void> {
  await getDiscussionSummary(7);
  await getMessages(7, 'T1', { limit: 50, periodKey: 202606 }, null);
  await getMessages(7, 'T1', { after: 10, limit: 100 }, '"5"');
  await getMessages(7, 'T1', { before: 4, limit: 50 }, null);
  await postMessage(7, 'T1', {
    body: 'hi',
    periodKey: 202606,
    clientMessageId: '3f2c1b9e-0000-4000-8000-000000000001',
    mentionUserIds: [3],
  });
  await editMessage(7, 'T1', 11, { body: 'hello', mentionUserIds: [3] });
  await deleteMessage(7, 'T1', 11);
  await markRead(7, 'T1', 12);
  await getMentionCandidates(7, 'T1', 202606, 'Ai');
  await getMentionTasks(20);
}

afterEach(() => {
  vi.unstubAllGlobals();
  calls.length = 0;
});

describe('table chat api.ts ↔ openapi.snapshot.json', () => {
  it('every call is an existing "method + path" pair of the contract', async () => {
    stubFetch();
    await callEverything();

    for (const call of calls) {
      const template = templateOf(call.path);
      expect(template, `${call.method} ${call.path}: no such path in the contract`).toBeDefined();
      expect(Object.keys(spec.paths[template ?? ''] ?? {}), `${call.method} ${call.path}: method not supported`).toContain(
        call.method.toLowerCase(),
      );
    }
  });

  it('request bodies match the schema: all required fields, nothing extra', async () => {
    stubFetch();
    await callEverything();

    const problems: string[] = [];
    for (const call of calls) {
      const operation = spec.paths[templateOf(call.path) ?? '']?.[call.method.toLowerCase()];
      const schema = operation?.requestBody?.content['application/json']?.schema;

      if (schema === undefined) {
        if (call.body !== undefined) problems.push(`${call.method} ${call.path}: the server expects no body`);
        continue;
      }
      problems.push(...violations(call.body, schema, `${call.method} ${call.path}`));
    }

    expect(problems).toEqual([]);
  });

  it('every table-chat action of the server has a client call', async () => {
    stubFetch();
    await callEverything();

    const covered = new Set(calls.map((c) => `${c.method.toLowerCase()} ${templateOf(c.path)}`));
    const server = Object.entries(spec.paths)
      .filter(([path]) => path.includes('/discussions') || path.includes('/messages') || path.includes('/mention-candidates') || path.endsWith('/read') || path.includes('/table-discussion-mentions'))
      .filter(([path]) => path.includes('/documents/') || path.startsWith('/api/v1/me/'))
      .flatMap(([path, methods]) => Object.keys(methods).map((m) => `${m} ${path}`));

    expect(server.length).toBe(8);
    expect(server.filter((action) => !covered.has(action))).toEqual([]);
  });

  it('the poll sends the previous ETag as If-None-Match', async () => {
    stubFetch();
    await getMessages(7, 'T1', { after: 10, limit: 100 }, '"5"');

    expect(calls[0]?.headers['if-none-match']).toBe('"5"');
  });

  it('encodes the table code and puts the query into the address', async () => {
    const fetchMock = vi.fn(() => Promise.resolve(new Response('{}', { status: 200, headers: { 'Content-Type': 'application/json' } })));
    vi.stubGlobal('fetch', fetchMock);

    await getMessages(7, 'A/B C', { after: 3, limit: 100, periodKey: 202606 }, null);

    expect(String((fetchMock.mock.calls[0] as unknown[])[0])).toBe(
      '/api/v1/documents/7/tables/A%2FB%20C/messages?after=3&limit=100&periodKey=202606',
    );
  });
});
