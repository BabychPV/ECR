import type { ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import type { DocumentTableDto } from '@/api/types';
import { Catalog } from '@/test/__tests__/a11yFixtures';
import { testTheme } from '@/test/render';
import { DiscussionPollMs } from '../api';
import { TableChatPanel } from '../TableChatPanel';
import { FakeChatServer, message } from './fakeChatServer';

/**
 * The table chat panel against an in-memory server: what is shown, the poll (ETag/304, revision → refetch of the
 * window, hidden tab), the composer (Ctrl+Enter, retry with the same GUID, @mention), edit window, read-only, mark-read.
 * Texts are the REAL catalog of `09-seed.sql`: a key without text would show as `⟦key⟧`.
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

const text = (key: string): string => Catalog[key] ?? `⟦${key}⟧`;

const Table = {
  allowsDynamicRows: false,
  maxDynamicRows: null,
  sheetCode: 'A',
  sheetDefId: 1,
  sheetNameL10n: { values: { en: 'Sheet A' } },
  sheetOrdinal: 0,
  tableCode: 'T1',
  tableDefId: 1,
  tableInstanceId: 10,
  tableNameL10n: { values: { en: '3.00 Stationary Equipment' } },
  tableOrdinal: 0,
} as DocumentTableDto;

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

function mount(onClose = vi.fn(), takeFocus = false): ReturnType<typeof vi.fn> {
  render(
    <Wrapper>
      <TableChatPanel documentId={7} periodKey={202606} table={Table} onClose={onClose} takeFocus={takeFocus} />
    </Wrapper>,
  );

  return onClose;
}

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
  vi.useRealTimers();
  Reflect.deleteProperty(document, 'visibilityState');
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

const messageEls = (): HTMLElement[] => Array.from(document.querySelectorAll<HTMLElement>('[data-chat-message]'));

describe('what the panel shows', () => {
  it('shows author, period tag, edited / deleted / new marks, mentions, and the body as plain text', async () => {
    server.lastRead = 1;
    server.messages = [
      message(1, { body: 'old one' }),
      message(2, {
        authorName: 'Aigerim Sadykova',
        authorUserId: 3,
        body: 'OK <b>bold?</b> @Aidar Nurlanov please',
        mentions: [{ userId: 2, name: 'Aidar Nurlanov' }],
        editedAt: '2026-06-01T11:00:00Z',
      }),
      message(3, { body: null, isDeleted: true, deletedAt: '2026-06-01T12:00:00Z' }),
      message(4, { authorUserId: 1, authorName: 'Me', body: 'mine' }),
    ];
    server.revision = 5;

    mount();
    await screen.findByText(/bold\?/);

    const rows = messageEls();
    expect(rows).toHaveLength(4);

    // The body is text: no <b> element exists, the markup is visible as characters.
    const second = rows[1] as HTMLElement;
    expect(second.querySelector('b')).toBeNull();
    expect(within(second).getByText(/<b>bold\?<\/b>/)).toBeTruthy();
    expect(second.querySelector('[data-chat-mentioned]')?.textContent).toBe('@Aidar Nurlanov');
    expect(second.querySelector('[data-chat-edited]')?.textContent).toBe(text('tableChat.edited'));
    expect(second.querySelector('[data-chat-period]')?.textContent).toContain('2026');

    expect((rows[2] as HTMLElement).querySelector('[data-chat-deleted]')?.textContent).toBe(text('tableChat.deleted'));

    // Unread: someone else's, after the read mark (1) — id 2 yes, 3 is deleted, 4 is mine, 1 is read.
    expect(rows.map((row) => row.hasAttribute('data-chat-unread'))).toEqual([false, true, false, false]);
    expect(screen.getAllByText(text('tableChat.new'))).toHaveLength(1);
  });

  it('asks for the latest page of the bound table first; "this period only" refetches with the period', async () => {
    server.messages = [message(1), message(2, { periodKey: 202605 })];

    mount();
    await screen.findByText('message 1');

    const first = server.of('GET', '/messages')[0];
    expect(first?.path).toBe('/api/v1/documents/7/tables/T1/messages');
    expect(first?.params.get('limit')).toBe('50');
    expect(first?.params.has('periodKey')).toBe(false);

    fireEvent.click(screen.getByRole('button', { name: /2026 only|only/i }));
    await waitFor(() => expect(messageEls()).toHaveLength(1));
    expect(server.of('GET', '/messages').at(-1)?.params.get('periodKey')).toBe('202606');
  });

  it('empty chat says so, and a read-only user gets no composer', async () => {
    server.canWrite = false;
    mount();

    expect(await screen.findByText(text('tableChat.empty'))).toBeTruthy();
    expect(screen.getByText(text('tableChat.readOnly'))).toBeTruthy();
    expect(screen.queryByRole('textbox')).toBeNull();
  });

  it('loads older messages above, keeping the newer ones', async () => {
    server.messages = Array.from({ length: 60 }, (_, i) => message(i + 1));
    mount();
    await screen.findByText('message 60');
    expect(messageEls()).toHaveLength(50);

    fireEvent.click(screen.getByRole('button', { name: text('tableChat.loadOlder') }));
    await waitFor(() => expect(messageEls()).toHaveLength(60));
    expect(server.of('GET', '/messages').at(-1)?.params.get('before')).toBe('11');
    expect(screen.queryByRole('button', { name: text('tableChat.loadOlder') })).toBeNull();
  });
});

describe('the composer', () => {
  async function composer(): Promise<HTMLTextAreaElement> {
    return (await screen.findByRole('textbox', { name: /Message/ })) as HTMLTextAreaElement;
  }

  it('Ctrl+Enter sends plain text with the period and a client GUID, then clears and shows the message', async () => {
    mount();
    const area = await composer();

    fireEvent.change(area, { target: { value: '  hello team  ' } });
    fireEvent.keyDown(area, { key: 'Enter', ctrlKey: true });

    await screen.findByText('hello team');
    const post = server.of('POST', '/messages')[0];
    expect(post?.body?.['body']).toBe('hello team');
    expect(post?.body?.['periodKey']).toBe(202606);
    expect(String(post?.body?.['clientMessageId'])).toMatch(/^[0-9a-f-]{36}$/);
    expect(post?.body).not.toHaveProperty('mentionUserIds');
    expect(area.value).toBe('');
  });

  it('plain Enter does not send', async () => {
    mount();
    const area = await composer();

    fireEvent.change(area, { target: { value: 'line' } });
    fireEvent.keyDown(area, { key: 'Enter' });

    expect(server.of('POST', '/messages')).toHaveLength(0);
  });

  it('a failed send keeps the text; the retry reuses the same GUID, a changed text takes a new one', async () => {
    mount();
    const area = await composer();
    server.failNextPost = true;

    fireEvent.change(area, { target: { value: 'will fail once' } });
    fireEvent.keyDown(area, { key: 'Enter', ctrlKey: true });
    await waitFor(() => expect(server.of('POST', '/messages')).toHaveLength(1));
    await waitFor(() => expect(area.value).toBe('will fail once'));

    fireEvent.keyDown(area, { key: 'Enter', ctrlKey: true });
    await waitFor(() => expect(server.of('POST', '/messages')).toHaveLength(2));
    await screen.findByText('will fail once');

    const [first, second] = server.of('POST', '/messages');
    expect(second?.body?.['clientMessageId']).toBe(first?.body?.['clientMessageId']);
    expect(server.messages).toHaveLength(1);

    fireEvent.change(area, { target: { value: 'another text' } });
    fireEvent.keyDown(area, { key: 'Enter', ctrlKey: true });
    await waitFor(() => expect(server.of('POST', '/messages')).toHaveLength(3));
    expect(server.of('POST', '/messages')[2]?.body?.['clientMessageId']).not.toBe(first?.body?.['clientMessageId']);
  });

  it('does not send an empty or too long message', async () => {
    mount();
    const area = await composer();
    const send = screen.getByRole('button', { name: text('tableChat.send') });

    expect((send as HTMLButtonElement).disabled).toBe(true);
    fireEvent.change(area, { target: { value: 'x'.repeat(4001) } });
    expect((send as HTMLButtonElement).disabled).toBe(true);
    expect(screen.getByText(text('tableChat.tooLong').replace('{max}', '4000'))).toBeTruthy();
    fireEvent.keyDown(area, { key: 'Enter', ctrlKey: true });
    expect(server.of('POST', '/messages')).toHaveLength(0);
  });

  it('@ offers people the server returned; Enter picks, and only a name still in the text is sent as a mention', async () => {
    server.candidates = [
      { userId: 7, name: 'Aigerim Sadykova' },
      { userId: 9, name: 'Aidar Nurlanov' },
    ];
    mount();
    const area = await composer();

    fireEvent.change(area, { target: { value: 'checked with @Ai' } });

    const list = await screen.findByRole('listbox', { name: text('tableChat.mentionList') }, { timeout: 3000 });
    expect(within(list).getAllByRole('option')).toHaveLength(2);
    expect(server.of('GET', '/mention-candidates').at(-1)?.params.get('q')).toBe('Ai');
    expect(server.of('GET', '/mention-candidates').at(-1)?.params.get('periodKey')).toBe('202606');

    fireEvent.keyDown(area, { key: 'ArrowDown' });
    fireEvent.keyDown(area, { key: 'Enter' });
    expect(area.value).toBe('checked with @Aidar Nurlanov ');
    expect(server.of('POST', '/messages')).toHaveLength(0);

    fireEvent.keyDown(area, { key: 'Enter', ctrlKey: true });
    await waitFor(() => expect(server.of('POST', '/messages')).toHaveLength(1));
    expect(server.of('POST', '/messages')[0]?.body?.['mentionUserIds']).toEqual([9]);
  });

  it('Esc inside the mention list closes the list only, not the panel', async () => {
    server.candidates = [{ userId: 7, name: 'Aigerim Sadykova' }];
    const onClose = mount();
    const area = await composer();

    fireEvent.change(area, { target: { value: '@Ai' } });
    await screen.findByRole('listbox', {}, { timeout: 3000 });
    fireEvent.keyDown(area, { key: 'Escape' });

    expect(screen.queryByRole('listbox')).toBeNull();
    expect(onClose).not.toHaveBeenCalled();
  });
});

describe('edit and delete', () => {
  it('offers Edit only inside the 15 minute window and sends the PATCH', async () => {
    const soon = new Date(Date.now() + 10 * 60_000).toISOString();
    const gone = new Date(Date.now() - 60_000).toISOString();
    server.messages = [
      message(1, { authorUserId: 1, canEdit: true, editableUntil: soon, body: 'fresh' }),
      message(2, { authorUserId: 1, canEdit: true, editableUntil: gone, body: 'stale' }),
    ];
    mount();
    await screen.findByText('fresh');

    const rows = messageEls();
    expect((rows[0] as HTMLElement).querySelector('[data-chat-edit]')).not.toBeNull();
    expect((rows[1] as HTMLElement).querySelector('[data-chat-edit]')).toBeNull();

    fireEvent.click((rows[0] as HTMLElement).querySelector('[data-chat-edit]') as HTMLElement);
    const area = screen.getByRole('textbox', { name: text('tableChat.editLabel') });
    fireEvent.change(area, { target: { value: 'fresh, fixed' } });
    fireEvent.click(screen.getByRole('button', { name: text('common.save') }));

    await screen.findByText('fresh, fixed');
    expect(server.of('PATCH', '/messages/1')[0]?.body).toEqual({ body: 'fresh, fixed', mentionUserIds: [] });
  });

  it('delete asks once more, then shows the placeholder; the moderator can delete other people\'s messages', async () => {
    server.canModerate = true;
    server.messages = [message(1, { canDelete: true, body: 'rude' })];
    mount();
    await screen.findByText('rude');

    fireEvent.click(document.querySelector('[data-chat-delete]') as HTMLElement);
    expect(server.of('DELETE', '/messages/1')).toHaveLength(0);
    fireEvent.click(document.querySelector('[data-chat-delete-yes]') as HTMLElement);

    await screen.findByText(text('tableChat.deleted'));
    expect(server.of('DELETE', '/messages/1')).toHaveLength(1);
    expect(screen.queryByText('rude')).toBeNull();
  });

  it('no Delete button when the server did not say canDelete', async () => {
    server.messages = [message(1, { canDelete: false })];
    mount();
    await screen.findByText('message 1');

    expect(document.querySelector('[data-chat-delete]')).toBeNull();
  });
});

describe('read mark', () => {
  it('marks the chat read up to the newest message once the panel has been open on it', async () => {
    vi.useFakeTimers();
    server.messages = [message(1), message(2)];
    mount();

    await act(async () => {
      await vi.advanceTimersByTimeAsync(100);
    });
    expect(messageEls()).toHaveLength(2);
    expect(server.of('PUT', '/read')).toHaveLength(0);

    await act(async () => {
      await vi.advanceTimersByTimeAsync(900);
    });
    expect(server.of('PUT', '/read')).toHaveLength(1);
    expect(server.of('PUT', '/read')[0]?.body).toEqual({ lastReadMessageId: 2 });
    expect(server.of('PUT', '/read')[0]?.path).toBe('/api/v1/documents/7/tables/T1/read');
  });
});

describe('the poll', () => {
  async function tick(ms: number): Promise<void> {
    await act(async () => {
      await vi.advanceTimersByTimeAsync(ms);
    });
  }

  beforeEach(() => {
    vi.useFakeTimers();
  });

  it('polls every 10 s with after=lastMessageId; 304 changes nothing; a new revision refetches the loaded window', async () => {
    expect(DiscussionPollMs).toBe(10_000);
    server.messages = [message(1), message(2)];
    mount();
    await tick(100);
    expect(messageEls()).toHaveLength(2);
    const loads = server.of('GET', '/messages').length;

    await tick(9_000);
    expect(server.of('GET', '/messages')).toHaveLength(loads);

    await tick(1_100);
    const firstPoll = server.of('GET', '/messages').at(-1);
    expect(firstPoll?.params.get('after')).toBe('2');
    // The poll reuses the ETag of the first load: nothing changed, so the answer is 304.
    expect(firstPoll?.headers['if-none-match']).toBe(`"${String(server.revision)}"`);
    expect(messageEls()).toHaveLength(2);

    // Unchanged chat: the second poll carries the ETag of the first and gets 304.
    await tick(10_000);
    const secondPoll = server.of('GET', '/messages').at(-1);
    expect(secondPoll?.headers['if-none-match']).toBe(`"${String(server.revision)}"`);

    // An OLD message is edited by somebody else: lastMessageId does not move, the revision does.
    server.messages[0] = { ...message(1), body: 'edited elsewhere', editedAt: '2026-06-01T11:00:00Z' };
    server.touch();
    await tick(10_000);
    expect(screen.getByText('edited elsewhere')).toBeTruthy();

    // A new message arrives.
    server.messages.push(message(3, { body: 'brand new' }));
    server.touch();
    await tick(10_000);
    expect(screen.getByText('brand new')).toBeTruthy();
    expect(messageEls()).toHaveLength(3);
  });

  it('does not poll while the tab is hidden and polls at once when it becomes visible', async () => {
    server.messages = [message(1)];
    let visibility = 'hidden';
    Object.defineProperty(document, 'visibilityState', { configurable: true, get: () => visibility });
    mount();
    await tick(100);
    const loads = server.of('GET', '/messages').length;

    await tick(45_000);
    expect(server.of('GET', '/messages')).toHaveLength(loads);

    server.messages.push(message(2, { body: 'while hidden' }));
    server.touch();
    visibility = 'visible';
    await act(async () => {
      document.dispatchEvent(new Event('visibilitychange'));
      await vi.advanceTimersByTimeAsync(100);
    });

    expect(screen.getByText('while hidden')).toBeTruthy();
  });

  it('scrolled up: new messages do not move the list or touch the composer; a pill says how many and brings the user down', async () => {
    server.messages = Array.from({ length: 5 }, (_, i) => message(i + 1));
    mount();
    await tick(100);
    const list = screen.getByRole('log');
    const area = screen.getByRole('textbox', { name: /Message/ }) as HTMLTextAreaElement;
    fireEvent.change(area, { target: { value: 'half-typed reply' } });

    // The user scrolls up to read: 1000 px of content, 200 px window, at the top.
    let top = 0;
    Object.defineProperty(list, 'scrollHeight', { configurable: true, get: () => 1000 });
    Object.defineProperty(list, 'clientHeight', { configurable: true, get: () => 200 });
    Object.defineProperty(list, 'scrollTop', {
      configurable: true,
      get: () => top,
      set: (value: number) => {
        top = value;
      },
    });
    fireEvent.scroll(list);
    expect(document.querySelector('[data-chat-new-pill]')).toBeNull();

    // Two messages by somebody else and one own arrive with the next poll.
    server.messages.push(message(6), message(7), message(8, { authorUserId: 1, authorName: 'Me' }));
    server.touch();
    await tick(10_000);

    expect(messageEls()).toHaveLength(8);
    expect(list.scrollTop).toBe(0);
    expect(area.value).toBe('half-typed reply');
    const pill = screen.getByRole('button', { name: text('tableChat.newBelow').replace('{n}', '2') });
    expect(pill.getAttribute('data-chat-new-pill')).toBe('2');

    fireEvent.click(pill);
    expect(list.scrollTop).toBe(1000);
    expect(document.querySelector('[data-chat-new-pill]')).toBeNull();
    expect(area.value).toBe('half-typed reply');
  });

  it('at the bottom the list follows new messages and no pill appears', async () => {
    server.messages = [message(1)];
    mount();
    await tick(100);

    server.messages.push(message(2, { body: 'next' }));
    server.touch();
    await tick(10_000);

    expect(screen.getByText('next')).toBeTruthy();
    expect(document.querySelector('[data-chat-new-pill]')).toBeNull();
  });

  it('stops polling when the panel closes (unmounts)', async () => {
    server.messages = [message(1)];
    mount();
    await tick(100);

    cleanup();
    const before = server.requests.length;
    await tick(60_000);

    expect(server.requests.length).toBe(before);
  });
});

describe('keyboard', () => {
  it('Esc closes the panel and asks to return the focus', async () => {
    const onClose = mount();
    await screen.findByText(text('tableChat.empty'));

    fireEvent.keyDown(screen.getByRole('complementary'), { key: 'Escape' });

    expect(onClose).toHaveBeenCalledWith(true);
  });

  it('the close button closes it too', async () => {
    const onClose = mount();
    await screen.findByText(text('tableChat.empty'));

    fireEvent.click(screen.getByRole('button', { name: text('tableChat.close') }));

    expect(onClose).toHaveBeenCalledWith(true);
  });

  it('opened by a human action, the composer takes the focus', async () => {
    mount(vi.fn(), true);
    const area = await screen.findByRole('textbox', { name: /Message/ });

    await waitFor(() => expect(document.activeElement).toBe(area));
  });
});
