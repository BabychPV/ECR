import type { ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, renderHook, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import type { DocumentTableDto } from '@/api/types';
import { queryKeys } from '@/api/queryKeys';
import { Catalog } from '@/test/__tests__/a11yFixtures';
import { testTheme } from '@/test/render';
import { deleteMessage, editMessage, getMentionCandidates, getMessages, markRead, postMessage, useMentionCandidates } from '../api';
import { chatKey, parseChatKey } from '../chatKey';
import { chatTableOverride, publishSummary, setChatTable } from '../chatStore';
import { mentionTaskHref } from '../MentionTasksSection';
import { TableChatBadge } from '../TableChatBadge';
import { TableChatPanel } from '../TableChatPanel';
import { FakeChatServer } from './fakeChatServer';

/**
 * TC02: a copy of a repeatable sheet has its OWN chat under the same table code. Every call carries `sheetCopyId` of
 * the copy (and nothing for the original), the counters are matched by the summary `key`, and the cache keys differ.
 */
vi.mock('@/shared/i18n', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/i18n')>()),
  t: (key: string, params?: Record<string, string | number>) =>
    (Catalog[key] ?? `⟦${key}⟧`).replace(/\{(\w+)\}/g, (match, name: string) =>
      params !== undefined && name in params ? String(params[name]) : match,
    ),
}));
vi.mock('@/shared/session/useSession', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/session/useSession')>()),
  useSession: () => ({ data: { userId: 1, permissions: ['Document.View', 'Document.Discuss'] } }),
}));

const Original = {
  allowsDynamicRows: false,
  copyOrdinal: 1,
  isCopyable: true,
  maxDynamicRows: null,
  sheetCode: 'A',
  sheetDefId: 1,
  sheetNameL10n: { values: { en: 'Sheet A' } },
  sheetOrdinal: 0,
  tableCode: 'T1',
  tableDefId: 1,
  tableInstanceId: 10,
  tableNameL10n: { values: { en: 'Stationary Equipment' } },
  tableOrdinal: 0,
} as DocumentTableDto;

const Copy = { ...Original, copyOrdinal: 2, sheetCopyId: 77, tableInstanceId: 11 } as DocumentTableDto;

const server = new FakeChatServer();

function Wrapper({ children }: { children: ReactNode }): ReactNode {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });

  return (
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <MemoryRouter>{children}</MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>
  );
}

function mount(table: DocumentTableDto): void {
  render(
    <Wrapper>
      <TableChatPanel documentId={7} periodKey={202606} table={table} onClose={vi.fn()} takeFocus={false} />
    </Wrapper>,
  );
}

const item = (tableCode: string, sheetCopyId: number | null, total: number, unread: number) => ({
  tableCode,
  key: chatKey(tableCode, sheetCopyId),
  sheetCopyId,
  total,
  unread,
  lastAt: null,
  lastMessageId: total,
  mentionedMe: false,
  revision: 1,
});

beforeEach(() => {
  server.messages = [];
  server.revision = 1;
  server.lastRead = 0;
  server.canWrite = true;
  server.canModerate = false;
  server.candidates = [];
  server.failNextPost = false;
  server.requests.length = 0;
  server.install();
});

afterEach(() => {
  cleanup();
  act(() => {
    publishSummary(7, null);
    setChatTable(null);
  });
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('chat key', () => {
  it('is the code for the original and КОД#id for a copy, and parses back', () => {
    expect(chatKey('T1', null)).toBe('T1');
    expect(chatKey('T1', 77)).toBe('T1#77');
    expect(parseChatKey('T1#77')).toEqual({ tableCode: 'T1', sheetCopyId: 77 });
    expect(parseChatKey('T1')).toEqual({ tableCode: 'T1', sheetCopyId: null });
    // `#instanceId` is the other `?table=` form: not a copy key.
    expect(parseChatKey('#15')).toEqual({ tableCode: '#15', sheetCopyId: null });
  });
});

describe('every route carries sheetCopyId of the copy, and nothing for the original', () => {
  const urls: string[] = [];

  beforeEach(() => {
    urls.length = 0;
    vi.stubGlobal(
      'fetch',
      vi.fn((input: RequestInfo | URL) => {
        urls.push(String(input));

        return Promise.resolve(new Response('{}', { status: 200, headers: { 'Content-Type': 'application/json' } }));
      }),
    );
  });

  const body = { body: 'hi', periodKey: 202606, clientMessageId: '3f2c1b9e-0000-4000-8000-000000000001', mentionUserIds: [] };

  it('copy: all six routes have ?sheetCopyId=77', async () => {
    await getMessages(7, 'T1', { limit: 50, periodKey: 202606, sheetCopyId: 77 }, null);
    await postMessage(7, 'T1', body, 77);
    await editMessage(7, 'T1', 5, { body: 'x', mentionUserIds: [] }, 77);
    await deleteMessage(7, 'T1', 5, 77);
    await markRead(7, 'T1', 5, 77);
    await getMentionCandidates(7, 'T1', 202606, 'Ai', 77);

    expect(urls).toEqual([
      '/api/v1/documents/7/tables/T1/messages?limit=50&periodKey=202606&sheetCopyId=77',
      '/api/v1/documents/7/tables/T1/messages?sheetCopyId=77',
      '/api/v1/documents/7/tables/T1/messages/5?sheetCopyId=77',
      '/api/v1/documents/7/tables/T1/messages/5?sheetCopyId=77',
      '/api/v1/documents/7/tables/T1/read?sheetCopyId=77',
      '/api/v1/documents/7/tables/T1/mention-candidates?q=Ai&periodKey=202606&sheetCopyId=77',
    ]);
  });

  it('original: no sheetCopyId anywhere', async () => {
    await getMessages(7, 'T1', { limit: 50 }, null);
    await postMessage(7, 'T1', body);
    await editMessage(7, 'T1', 5, { body: 'x', mentionUserIds: [] });
    await deleteMessage(7, 'T1', 5);
    await markRead(7, 'T1', 5);
    await getMentionCandidates(7, 'T1', 202606, 'Ai');

    expect(urls.filter((url) => url.includes('sheetCopyId'))).toEqual([]);
  });
});

describe('the panel bound to a copy', () => {
  it('reads and writes the chat of THAT copy: sheetCopyId on the load and on the post', async () => {
    mount(Copy);
    const area = (await screen.findByRole('textbox', { name: /Message/ })) as HTMLTextAreaElement;

    expect(server.of('GET', '/messages')[0]?.params.get('sheetCopyId')).toBe('77');

    fireEvent.change(area, { target: { value: 'hello copy' } });
    fireEvent.keyDown(area, { key: 'Enter', ctrlKey: true });
    await screen.findByText('hello copy');

    expect(server.of('POST', '/messages')[0]?.params.get('sheetCopyId')).toBe('77');
  });

  it('the original panel sends no sheetCopyId', async () => {
    mount(Original);
    await screen.findByRole('textbox', { name: /Message/ });

    expect(server.of('GET', '/messages').every((request) => !request.params.has('sheetCopyId'))).toBe(true);
  });
});

describe('badge counters are matched by the summary key', () => {
  it('a copy and its original show their own counters', () => {
    act(() => publishSummary(7, [item('T1', null, 3, 1), item('T1', 77, 12, 5)]));

    render(
      <>
        <div data-testid="orig">
          <TableChatBadge documentId={7} tableCode="T1" />
        </div>
        <div data-testid="copy">
          <TableChatBadge documentId={7} tableCode="T1" sheetCopyId={77} />
        </div>
      </>,
    );

    expect(screen.getByTestId('orig').querySelector('[data-chat-badge]')?.getAttribute('aria-label')).toBe(
      (Catalog['tableChat.badgeLabel'] ?? '').replace('{total}', '3').replace('{unread}', '1'),
    );
    expect(screen.getByTestId('copy').querySelector('[data-chat-badge]')?.getAttribute('aria-label')).toBe(
      (Catalog['tableChat.badgeLabel'] ?? '').replace('{total}', '12').replace('{unread}', '5'),
    );
  });

  it('a copy without messages shows no badge even when its original has some', () => {
    act(() => publishSummary(7, [item('T1', null, 3, 1)]));

    const { container } = render(<TableChatBadge documentId={7} tableCode="T1" sheetCopyId={77} />);

    expect(container.innerHTML).toBe('');
  });

  it('a click records the KEY of the copy, not the bare code', () => {
    act(() => publishSummary(7, [item('T1', 77, 2, 0)]));
    const { container } = render(<TableChatBadge documentId={7} tableCode="T1" sheetCopyId={77} />);

    fireEvent.click(container.querySelector('[data-chat-badge]') as HTMLElement);

    expect(chatTableOverride()).toBe('T1#77');
  });
});

describe('cache keys and links', () => {
  it('mention candidates of a copy and of its original are different queries (no cache collision)', async () => {
    server.candidates = [{ userId: 3, name: 'Aigerim' }];
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const wrapper = ({ children }: { children: ReactNode }): ReactNode => (
      <QueryClientProvider client={client}>{children}</QueryClientProvider>
    );

    const original = renderHook(() => useMentionCandidates(7, 'T1', 202606, 'Ai', null), { wrapper });
    await waitFor(() => expect(original.result.current.isSuccess).toBe(true));
    const copy = renderHook(() => useMentionCandidates(7, 'T1', 202606, 'Ai', 77), { wrapper });
    await waitFor(() => expect(copy.result.current.isSuccess).toBe(true));

    expect(server.of('GET', '/mention-candidates').map((request) => request.params.get('sheetCopyId'))).toEqual([null, '77']);
    expect(client.getQueryCache().findAll({ queryKey: queryKeys.tableChat.all() })).toHaveLength(2);
  });

  it('a mention in a copy leads to the copy; a mention in the original keeps the plain code', () => {
    const base = { documentId: 7, periodKey: 202606, tableCode: 'T1' };

    expect(new URL(mentionTaskHref({ ...base, sheetCopyId: 77 }), 'http://x').searchParams.get('table')).toBe('T1#77');
    expect(new URL(mentionTaskHref({ ...base, sheetCopyId: null }), 'http://x').searchParams.get('table')).toBe('T1');
    expect(new URL(mentionTaskHref(base), 'http://x').searchParams.get('table')).toBe('T1');
  });
});
