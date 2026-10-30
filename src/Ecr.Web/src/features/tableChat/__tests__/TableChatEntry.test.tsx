import type { ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, useLocation } from 'react-router-dom';
import type { DocumentTableDto } from '@/api/types';
import { clearInspectedCell, publishInspectedCell } from '@/features/documents/inspector/inspectedCell';
import { Catalog } from '@/test/__tests__/a11yFixtures';
import { testTheme } from '@/test/render';
import { publishSummary, setChatTable } from '../chatStore';
import { TableChatBadge } from '../TableChatBadge';
import { TableChatButton, TableChatHost } from '../TableChatEntry';
import { FakeChatServer, message } from './fakeChatServer';

/**
 * The entry of the chat on the document page: the header button, the badge in a table title and the panel host
 * with its table binding (explicit choice → focused cell → `?table=` → first table of the sheet; hidden tables never).
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

function table(code: string, id: number, ordinal: number): DocumentTableDto {
  return {
    allowsDynamicRows: false,
    maxDynamicRows: null,
    sheetCode: 'A',
    sheetDefId: 1,
    sheetNameL10n: { values: { en: 'Sheet A' } },
    sheetOrdinal: 0,
    tableCode: code,
    tableDefId: id,
    tableInstanceId: id * 10,
    tableNameL10n: { values: { en: `Table ${code}` } },
    tableOrdinal: ordinal,
  } as DocumentTableDto;
}

const Visible = [table('T1', 1, 0), table('T2', 2, 1)];

const server = new FakeChatServer();
let summary: Response | (() => Response) = new Response('{}', { status: 404 });

function Where(): ReactNode {
  const location = useLocation();

  return <output data-testid="where">{location.search}</output>;
}

function Shell({ children, at = '/documents/7' }: { children: ReactNode; at?: string }): ReactNode {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  return (
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={[at]}>
          {children}
          <Where />
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>
  );
}

function summaryOf(items: unknown[], canWrite = true): Response {
  return new Response(JSON.stringify({ items, canWrite }), { status: 200, headers: { 'Content-Type': 'application/json' } });
}

beforeEach(() => {
  server.messages = [];
  server.requests.length = 0;
  server.install();
  const chat = globalThis.fetch;
  vi.stubGlobal(
    'fetch',
    vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
      if (String(input).endsWith('/discussions/summary')) {
        return Promise.resolve(typeof summary === 'function' ? summary() : summary.clone());
      }

      return chat(input, init);
    }),
  );
});

afterEach(() => {
  cleanup();
  act(() => {
    clearInspectedCell();
    setChatTable(null);
    publishSummary(7, null);
  });
  vi.unstubAllGlobals();
});

describe('the header button', () => {
  it('is not rendered when the summary fails (no chat for this user / server) — the page is not an error', async () => {
    summary = new Response('{}', { status: 404, headers: { 'Content-Type': 'application/problem+json' } });
    render(
      <Shell>
        <TableChatButton documentId={7} />
      </Shell>,
    );

    await waitFor(() => expect(globalThis.fetch).toHaveBeenCalled());
    expect(screen.queryByRole('button')).toBeNull();
  });

  it('is not rendered for a malformed body or for a read-only user of a document with no discussions', async () => {
    summary = () => new Response('{}', { status: 200, headers: { 'Content-Type': 'application/json' } });
    const { unmount } = render(
      <Shell>
        <TableChatButton documentId={7} />
      </Shell>,
    );
    await waitFor(() => expect(globalThis.fetch).toHaveBeenCalled());
    expect(screen.queryByRole('button')).toBeNull();
    unmount();

    // A page test that stubs every unknown route with `null` must not crash the page (the button renders nothing).
    summary = () => new Response('null', { status: 200, headers: { 'Content-Type': 'application/json' } });
    const nullBody = render(
      <Shell>
        <TableChatButton documentId={7} />
      </Shell>,
    );
    await waitFor(() => expect(globalThis.fetch).toHaveBeenCalledTimes(2));
    expect(screen.queryByRole('button')).toBeNull();
    nullBody.unmount();

    summary = () => summaryOf([], false);
    render(
      <Shell>
        <TableChatButton documentId={7} />
      </Shell>,
    );
    await waitFor(() => expect(globalThis.fetch).toHaveBeenCalledTimes(3));
    expect(screen.queryByRole('button')).toBeNull();
  });

  it('shows the total unread of the document as a counter and in its name; a click opens and closes the panel', async () => {
    summary = () =>
      summaryOf([
        { tableCode: 'T1', total: 12, unread: 3, lastAt: '2026-06-01T10:00:00Z', lastMessageId: 12, mentionedMe: false, revision: 1 },
        { tableCode: 'T2', total: 2, unread: 1, lastAt: '2026-06-01T10:00:00Z', lastMessageId: 14, mentionedMe: true, revision: 1 },
      ]);
    render(
      <Shell>
        <TableChatButton documentId={7} />
      </Shell>,
    );

    const button = await screen.findByRole('button', { name: 'Discussion, 4 unread' });
    expect(button.querySelector('[data-chat-unread-total]')?.textContent).toBe('4');
    expect(button.getAttribute('aria-pressed')).toBe('false');
    expect(button.getAttribute('title')).toBe(Catalog['tableChat.buttonMentioned']);

    fireEvent.click(button);
    expect(screen.getByTestId('where').textContent).toBe('?panel=discussion');
    expect(button.getAttribute('aria-pressed')).toBe('true');

    fireEvent.click(button);
    expect(screen.getByTestId('where').textContent).toBe('');
  });

  it('without unread messages the name is plain "Discussion" and there is no counter', async () => {
    summary = () => summaryOf([{ tableCode: 'T1', total: 2, unread: 0, lastAt: null, lastMessageId: 2, mentionedMe: false, revision: 1 }]);
    render(
      <Shell>
        <TableChatButton documentId={7} />
      </Shell>,
    );

    const button = await screen.findByRole('button', { name: 'Discussion' });
    expect(button.querySelector('[data-chat-unread-total]')).toBeNull();
  });

  it('publishes the per-table counters for the badges and withdraws them on unmount', async () => {
    summary = () =>
      summaryOf([{ tableCode: 'T1', total: 12, unread: 3, lastAt: null, lastMessageId: 12, mentionedMe: false, revision: 1 }]);
    const { unmount } = render(
      <Shell>
        <TableChatButton documentId={7} />
        <TableChatBadge documentId={7} tableCode="T1" />
        <TableChatBadge documentId={7} tableCode="T2" />
      </Shell>,
    );

    const badge = await screen.findByRole('button', { name: 'Open discussion: 12 messages, 3 new' });
    expect(badge.textContent).toBe('12 · 3 new');
    expect(document.querySelectorAll('[data-chat-badge]')).toHaveLength(1);

    unmount();
    render(
      <Shell>
        <TableChatBadge documentId={7} tableCode="T1" />
      </Shell>,
    );
    expect(document.querySelectorAll('[data-chat-badge]')).toHaveLength(0);
  });
});

describe('the badge in a table title', () => {
  it('shows only the total when nothing is new, and nothing for a table of another document', () => {
    act(() =>
      publishSummary(7, [{ tableCode: 'T1', total: 5, unread: 0, lastAt: null, lastMessageId: 5, mentionedMe: false, revision: 1 }]),
    );
    render(
      <Shell>
        <TableChatBadge documentId={7} tableCode="T1" />
        <TableChatBadge documentId={8} tableCode="T1" />
      </Shell>,
    );

    const badges = document.querySelectorAll('[data-chat-badge]');
    expect(badges).toHaveLength(1);
    expect(badges[0]?.textContent).toBe('5');
    expect(badges[0]?.hasAttribute('data-unread')).toBe(false);
  });

  it('a click opens the panel on THIS table, whatever cell was focused', async () => {
    act(() =>
      publishSummary(7, [{ tableCode: 'T2', total: 5, unread: 2, lastAt: null, lastMessageId: 5, mentionedMe: false, revision: 1 }]),
    );
    act(() =>
      publishInspectedCell({
        tableInstanceId: 10,
        periodKey: 202606,
        rowKey: 'R1',
        rowLabel: 'r',
        columnCode: 'C1',
        columnDefId: 1,
        columnHeader: 'c',
        unitSymbol: null,
        dataType: 'Text',
        scale: null,
        isCalculated: false,
        isReadOnly: false,
        value: '',
      }),
    );
    render(
      <Shell>
        <TableChatBadge documentId={7} tableCode="T2" />
        <TableChatHost documentId={7} periodKey={202606} tables={Visible} activeTables={Visible} />
      </Shell>,
    );

    fireEvent.click(document.querySelector('[data-chat-badge]') as HTMLElement);

    const panel = await screen.findByRole('complementary');
    expect(panel.getAttribute('data-chat-table')).toBe('T2');
    expect(screen.getByTestId('where').textContent).toBe('?panel=discussion');
  });
});

describe('the panel host binds to the right table', () => {
  function host(at: string, tables = Visible): void {
    render(
      <Shell at={at}>
        <TableChatHost documentId={7} periodKey={202606} tables={tables} activeTables={tables} />
      </Shell>,
    );
  }

  it('renders nothing while the address has no ?panel=discussion', () => {
    host('/documents/7?panel=issues');

    expect(screen.queryByRole('complementary')).toBeNull();
    expect(server.requests).toHaveLength(0);
  });

  it('follows the table of the last focused cell', async () => {
    act(() =>
      publishInspectedCell({
        tableInstanceId: 20,
        periodKey: 202606,
        rowKey: 'R1',
        rowLabel: 'r',
        columnCode: 'C1',
        columnDefId: 1,
        columnHeader: 'c',
        unitSymbol: null,
        dataType: 'Text',
        scale: null,
        isCalculated: false,
        isReadOnly: false,
        value: '',
      }),
    );
    host('/documents/7?panel=discussion&table=T1');

    const panel = await screen.findByRole('complementary');
    expect(panel.getAttribute('data-chat-table')).toBe('T2');
  });

  it('falls back to ?table=, then to the first table of the sheet', async () => {
    host('/documents/7?panel=discussion&table=T2');
    expect((await screen.findByRole('complementary')).getAttribute('data-chat-table')).toBe('T2');
    cleanup();

    host('/documents/7?panel=discussion');
    expect((await screen.findByRole('complementary')).getAttribute('data-chat-table')).toBe('T1');
  });

  it('never opens a chat for a table the user does not see (?table= names a hidden one)', async () => {
    server.messages = [message(1)];
    host('/documents/7?panel=discussion&table=HIDDEN');

    const panel = await screen.findByRole('complementary');
    expect(panel.getAttribute('data-chat-table')).toBe('T1');
    await screen.findByText('message 1');
    expect(server.requests.some((request) => request.path.includes('/HIDDEN/'))).toBe(false);
  });

  it('with no visible tables there is no panel at all', () => {
    host('/documents/7?panel=discussion', []);

    expect(screen.queryByRole('complementary')).toBeNull();
    expect(server.requests).toHaveLength(0);
  });

  it('the close button removes ?panel= from the address', async () => {
    host('/documents/7?panel=discussion&table=T1');
    await screen.findByRole('complementary');

    fireEvent.click(screen.getByRole('button', { name: Catalog['tableChat.close'] ?? '' }));

    await waitFor(() => expect(screen.queryByRole('complementary')).toBeNull());
    expect(screen.getByTestId('where').textContent).toBe('?table=T1');
  });
});
