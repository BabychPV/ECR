import type { JSX } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { DocumentTableDto } from '@/api/types';
import { formatTime } from '@/shared/format/datetime';
import { Catalog } from '@/test/__tests__/a11yFixtures';
import { SheetChangesPollMs } from '../api';
import { useSheetLiveRefresh } from '../SheetLiveRefresh';

/**
 * LIVE-REFRESH B2: the change marker of the sheet — 30 s cadence on a visible tab only, the marker chain (`since`),
 * the polite banner about OTHER people's changes (unknown author → generic text), `404`/`422` stop the poll, and the
 * banner's Refresh re-reads only the announced tables. Texts are the REAL catalog of `09-seed.sql`.
 */
vi.mock('@/shared/i18n', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/i18n')>()),
  t: (key: string, params?: Record<string, string | number>) =>
    (Catalog[key] ?? `⟦${key}⟧`).replace(/\{(\w+)\}/g, (match, name: string) =>
      params !== undefined && name in params ? String(params[name]) : match,
    ),
}));

const text = (key: string): string => Catalog[key] ?? `⟦${key}⟧`;

const Period = 202610;

function table(code: string, id: number): DocumentTableDto {
  return {
    allowsDynamicRows: false,
    maxDynamicRows: null,
    sheetCode: 'S1',
    sheetDefId: 5,
    sheetNameL10n: { values: { en: 'Sheet' } },
    sheetOrdinal: 1,
    tableCode: code,
    tableDefId: id,
    tableInstanceId: id,
    tableNameL10n: { values: { en: code } },
    tableOrdinal: id,
  };
}

const Tables = [table('T1', 11), table('T2', 12)];

interface Answer {
  readonly status?: number;
  readonly changed?: boolean;
  readonly by?: string | null;
  readonly byId?: number | null;
  readonly at?: string | null;
  readonly tables?: readonly { tableCode: string; tableInstanceId: number }[];
}

let answers: Answer[] = [];
let seq = 0;
const requests: URL[] = [];

function install(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn((input: RequestInfo | URL) => {
      const url = new URL(String(input), 'http://localhost');
      if (!url.pathname.endsWith('/changes')) return Promise.resolve(new Response('{}', { status: 200 }));

      requests.push(url);
      const answer = answers.shift() ?? {};
      if (answer.status !== undefined && answer.status !== 200) {
        return Promise.resolve(
          new Response(JSON.stringify({ title: 'x', status: answer.status }), {
            status: answer.status,
            headers: { 'Content-Type': 'application/problem+json' },
          }),
        );
      }

      seq += 1;
      const changed = answer.changed ?? false;
      const body = {
        marker: `m${String(seq)}`,
        changed,
        lastChangedByUserId: changed ? (answer.byId ?? null) : null,
        lastChangedBy: changed ? (answer.by ?? null) : null,
        lastChangedAt: changed ? (answer.at ?? null) : null,
        changedTables: changed ? (answer.tables ?? []) : [],
      };

      return Promise.resolve(new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } }));
    }),
  );
}

let client: QueryClient;

function Probe(): JSX.Element {
  const live = useSheetLiveRefresh({ documentId: 7, periodKey: Period, tables: Tables });

  return (
    <>
      {live.banner}
      {live.button}
      <input data-testid="editor" />
    </>
  );
}

function mount(): void {
  render(
    <MantineProvider>
      <QueryClientProvider client={client}>
        <Probe />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

async function tick(ms: number): Promise<void> {
  await act(async () => {
    await vi.advanceTimersByTimeAsync(ms);
  });
}

const banner = (): HTMLElement | null => document.querySelector('[data-testid="sheet-live-banner"]');

beforeEach(() => {
  vi.useFakeTimers();
  answers = [];
  seq = 0;
  requests.length = 0;
  client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  install();
});

afterEach(() => {
  cleanup();
  client.clear();
  vi.useRealTimers();
  Reflect.deleteProperty(document, 'visibilityState');
  vi.unstubAllGlobals();
});

describe('change marker poll (LIVE-REFRESH B2)', () => {
  it('first call takes only the marker; every 30 s the previous marker goes as since', async () => {
    expect(SheetChangesPollMs).toBe(30_000);
    mount();
    await tick(10);
    expect(requests).toHaveLength(1);
    expect(requests[0]?.pathname).toBe('/api/v1/documents/7/sheets/5/changes');
    expect(requests[0]?.searchParams.get('periodKey')).toBe(String(Period));
    expect(requests[0]?.searchParams.has('since')).toBe(false);

    await tick(29_000);
    expect(requests).toHaveLength(1);
    await tick(1_100);
    expect(requests).toHaveLength(2);
    expect(requests[1]?.searchParams.get('since')).toBe('m1');

    await tick(30_000);
    expect(requests[2]?.searchParams.get('since')).toBe('m2');
    expect(banner()).toBeNull();
  });

  it('a hidden tab does not poll; coming back asks at once', async () => {
    let visibility: DocumentVisibilityState = 'visible';
    Object.defineProperty(document, 'visibilityState', { configurable: true, get: () => visibility });
    mount();
    await tick(10);
    visibility = 'hidden';
    await tick(150_000);
    expect(requests).toHaveLength(1);

    visibility = 'visible';
    await act(async () => {
      document.dispatchEvent(new Event('visibilitychange'));
      await vi.advanceTimersByTimeAsync(10);
    });
    expect(requests).toHaveLength(2);
    expect(requests[1]?.searchParams.get('since')).toBe('m1');
  });

  it('somebody else changed the sheet: a polite banner with the name and the local time', async () => {
    const at = '2026-10-30T09:05:00Z';
    answers = [{}, { changed: true, by: 'Aigerim Sadykova', byId: 3, at, tables: [{ tableCode: 'T2', tableInstanceId: 12 }] }];
    mount();
    await tick(10);
    await tick(30_000);

    const host = screen.getByTestId('sheet-live-host');
    expect(host.getAttribute('role')).toBe('status');
    expect(host.getAttribute('aria-live')).toBe('polite');
    expect(banner()?.textContent).toContain(
      text('liveRefresh.updatedBy').replace('{user}', 'Aigerim Sadykova').replace('{time}', formatTime(at)),
    );
    expect(banner()?.getAttribute('data-live-tables')).toBe('12');
  });

  it('unknown author (deleted row, system write) — the generic text, never a made-up name', async () => {
    const at = '2026-10-30T09:05:00Z';
    answers = [{}, { changed: true, by: null, byId: null, at, tables: [{ tableCode: 'T1', tableInstanceId: 11 }] }];
    mount();
    await tick(10);
    await tick(30_000);

    expect(banner()?.textContent).toContain(text('liveRefresh.updatedByOther').replace('{time}', formatTime(at)));
  });

  it('own edits (the server answers changed=false) never raise the banner', async () => {
    answers = [{}, { changed: false }, { changed: false }];
    mount();
    await tick(10);
    await tick(60_000);

    expect(requests).toHaveLength(3);
    expect(banner()).toBeNull();
  });

  it.each([404, 422])('%i stops the poll for this sheet and period — no banner, no retry storm', async (status) => {
    answers = [{}, { status }];
    mount();
    await tick(10);
    await tick(30_000);
    expect(requests).toHaveLength(2);

    await tick(300_000);
    expect(requests).toHaveLength(2);
    expect(banner()).toBeNull();
  });

  it('a 500 is silent and the next tick asks again with the same marker', async () => {
    answers = [{}, { status: 500 }, {}];
    mount();
    await tick(10);
    await tick(30_000);
    await tick(30_000);

    expect(requests).toHaveLength(3);
    expect(requests[2]?.searchParams.get('since')).toBe('m1');
  });

  it('the banner\'s Refresh re-reads ONLY the announced tables, then hides the banner', async () => {
    client.setQueryData(['table-slice', 11, Period], { tableInstanceId: 11 });
    client.setQueryData(['table-slice', 12, Period], { tableInstanceId: 12 });
    answers = [{}, { changed: true, by: 'B', byId: 2, at: '2026-10-30T09:05:00Z', tables: [{ tableCode: 'T2', tableInstanceId: 12 }] }];
    mount();
    await tick(10);
    await tick(30_000);

    fireEvent.click(screen.getByTestId('sheet-live-refresh'));
    await tick(300);

    expect(client.getQueryState(['table-slice', 12, Period])?.isInvalidated).toBe(true);
    expect(client.getQueryState(['table-slice', 11, Period])?.isInvalidated).toBe(false);
    expect(banner()).toBeNull();
  });

  it('toolbar Refresh and Shift+F5 move the marker to now and re-read every table of the sheet; not from an input', async () => {
    client.setQueryData(['table-slice', 11, Period], { tableInstanceId: 11 });
    client.setQueryData(['table-slice', 12, Period], { tableInstanceId: 12 });
    mount();
    await tick(10);

    // From an input the key belongs to typing.
    const editor = screen.getByTestId('editor');
    fireEvent.keyDown(editor, { key: 'F5', code: 'F5', shiftKey: true });
    await tick(300);
    expect(requests).toHaveLength(1);
    expect(client.getQueryState(['table-slice', 11, Period])?.isInvalidated).toBe(false);

    const event = new KeyboardEvent('keydown', { key: 'F5', code: 'F5', shiftKey: true, bubbles: true, cancelable: true });
    act(() => {
      document.body.dispatchEvent(event);
    });
    await tick(300);
    expect(event.defaultPrevented).toBe(true);
    // The rebase call carries the marker of the first one.
    expect(requests).toHaveLength(2);
    expect(requests[1]?.searchParams.get('since')).toBe('m1');
    expect(client.getQueryState(['table-slice', 11, Period])?.isInvalidated).toBe(true);
    expect(client.getQueryState(['table-slice', 12, Period])?.isInvalidated).toBe(true);

    const button = screen.getByTestId('sheet-refresh');
    expect(button.getAttribute('aria-keyshortcuts')).toBe('Shift+F5');
    expect(button.textContent).toBe(text('liveRefresh.refresh'));
  });
});
