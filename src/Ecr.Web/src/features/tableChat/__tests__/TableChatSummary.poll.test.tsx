import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { Catalog } from '@/test/__tests__/a11yFixtures';
import { testTheme } from '@/test/render';
import { SummaryPollMs } from '../api';
import { publishSummary } from '../chatStore';
import { TableChatBadge } from '../TableChatBadge';
import { TableChatButton } from '../TableChatEntry';

/**
 * LIVE-REFRESH A1/A4: the summary (header button + badges in table titles) polls every 30 s while the tab is
 * visible, sends the previous ETag, keeps the badge on `304`, does not poll in a hidden tab and asks at once when
 * the tab comes back. ONE poller for the page: the badges read what the button published.
 */
vi.mock('@/shared/i18n', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/i18n')>()),
  t: (key: string, params?: Record<string, string | number>) =>
    (Catalog[key] ?? `⟦${key}⟧`).replace(/\{(\w+)\}/g, (match, name: string) =>
      params !== undefined && name in params ? String(params[name]) : match,
    ),
}));

interface Seen {
  readonly ifNoneMatch: string | null;
}

let revision = 1;
let unread = 1;
const seen: Seen[] = [];

function body(): string {
  return JSON.stringify({
    items: [{ tableCode: 'T1', total: 3, unread, lastAt: null, lastMessageId: 3, mentionedMe: false, revision }],
    canWrite: true,
  });
}

function install(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      if (!url.endsWith('/discussions/summary')) return Promise.resolve(new Response('{}', { status: 404 }));

      const ifNoneMatch = new Headers(init?.headers).get('If-None-Match');
      seen.push({ ifNoneMatch });
      const etag = `"r${String(revision)}-${String(unread)}"`;
      if (ifNoneMatch === etag) return Promise.resolve(new Response(null, { status: 304, headers: { ETag: etag } }));

      return Promise.resolve(new Response(body(), { status: 200, headers: { 'Content-Type': 'application/json', ETag: etag } }));
    }),
  );
}

function mount(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <MemoryRouter>
          <TableChatButton documentId={7} />
          <TableChatBadge documentId={7} tableCode="T1" />
          <TableChatBadge documentId={7} tableCode="T1" />
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

async function tick(ms: number): Promise<void> {
  await act(async () => {
    await vi.advanceTimersByTimeAsync(ms);
  });
}

beforeEach(() => {
  vi.useFakeTimers();
  revision = 1;
  unread = 1;
  seen.length = 0;
  install();
});

afterEach(() => {
  cleanup();
  act(() => publishSummary(7, null));
  vi.useRealTimers();
  Reflect.deleteProperty(document, 'visibilityState');
  vi.unstubAllGlobals();
});

describe('summary poll (LIVE-REFRESH A1)', () => {
  it('asks every 30 s with If-None-Match; 304 keeps the badge; a new message shows within one interval', async () => {
    expect(SummaryPollMs).toBe(30_000);
    mount();
    await tick(10);
    expect(seen).toHaveLength(1);
    expect(seen[0]?.ifNoneMatch).toBeNull();
    expect(screen.getAllByRole('button', { name: 'Open discussion: 3 messages, 1 new' })).toHaveLength(2);

    await tick(29_000);
    expect(seen).toHaveLength(1);

    await tick(1_100);
    // ONE request per interval although two badges and the button are mounted: a single shared query.
    expect(seen).toHaveLength(2);
    expect(seen[1]?.ifNoneMatch).toBe('"r1-1"');
    // 304: the badge is still there, unchanged.
    expect(screen.getAllByRole('button', { name: 'Open discussion: 3 messages, 1 new' })).toHaveLength(2);

    // Somebody writes: the revision and the counters move.
    revision = 2;
    unread = 2;
    await tick(30_000);
    expect(seen).toHaveLength(3);
    expect(screen.getAllByRole('button', { name: 'Open discussion: 3 messages, 2 new' })).toHaveLength(2);
    expect(screen.getByRole('button', { name: 'Discussion, 2 unread' })).toBeTruthy();
  });

  it('a hidden tab does not poll; coming back asks at once', async () => {
    let visibility: DocumentVisibilityState = 'visible';
    Object.defineProperty(document, 'visibilityState', { configurable: true, get: () => visibility });
    mount();
    await tick(10);
    expect(seen).toHaveLength(1);

    visibility = 'hidden';
    await act(async () => {
      // As the browser fires it: at the document, bubbling (TanStack listens on `window`).
      document.dispatchEvent(new Event('visibilitychange', { bubbles: true }));
      await vi.advanceTimersByTimeAsync(10);
    });
    await tick(120_000);
    expect(seen).toHaveLength(1);

    unread = 5;
    visibility = 'visible';
    await act(async () => {
      document.dispatchEvent(new Event('visibilitychange', { bubbles: true }));
      await vi.advanceTimersByTimeAsync(10);
    });
    expect(seen).toHaveLength(2);
    expect(screen.getByRole('button', { name: 'Discussion, 5 unread' })).toBeTruthy();
  });
});
