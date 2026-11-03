import type { JSX } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, render } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider, useQuery } from '@tanstack/react-query';
import type { DocumentTableDto } from '@/api/types';
import { useStatusStale } from '@/features/workflow/statusStale';
import { Catalog } from '@/test/__tests__/a11yFixtures';
import { useSheetLiveRefresh } from '../SheetLiveRefresh';

/**
 * LIVE-STATUS: a transition of the sheet by someone else (new `statusRevision` in the change marker) re-reads the
 * state (summary, slices, submission target), holds the action bar disabled until the new state is in, and says
 * "Status changed by <user>: <state>" politely; an unchanged marker (`304` on `If-None-Match`) does nothing.
 * Texts are the REAL seed catalog.
 */
vi.mock('@/shared/i18n', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/i18n')>()),
  t: (key: string, params?: Record<string, string | number>) =>
    (Catalog[key] ?? `⟦${key}⟧`).replace(/\{(\w+)\}/g, (match, name: string) =>
      params !== undefined && name in params ? String(params[name]) : match,
    ),
}));

const Period = 202610;

const Tables: DocumentTableDto[] = [
  {
    allowsDynamicRows: false,
    copyOrdinal: 1,
    isCopyable: false,
    maxDynamicRows: null,
    sheetCode: 'S1',
    sheetDefId: 5,
    sheetNameL10n: { values: { en: 'Sheet' } },
    sheetOrdinal: 1,
    tableCode: 'T1',
    tableDefId: 11,
    tableInstanceId: 11,
    tableNameL10n: { values: { en: 'T1' } },
    tableOrdinal: 1,
  },
];

interface Answer {
  readonly status?: number;
  readonly revision: string;
  readonly state: string;
  readonly statusChanged?: boolean;
}

let answers: Answer[] = [];
let sheetState = 'Draft';
let releaseSummary: (() => void) | null = null;
const changeRequests: Request[] = [];
let summaryReads = 0;

function json(body: unknown, init: ResponseInit = {}): Response {
  return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' }, ...init });
}

function install(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
      const url = new URL(String(input), 'http://localhost');
      if (url.pathname.endsWith('/changes')) {
        changeRequests.push(new Request(url, init));
        const answer = answers.shift() ?? { revision: 'r0', state: 'Draft' };
        if (answer.status === 304) return Promise.resolve(new Response(null, { status: 304 }));

        return Promise.resolve(
          json(
            {
              marker: `m-${answer.revision}`,
              changed: false,
              lastChangedByUserId: null,
              lastChangedBy: null,
              lastChangedAt: null,
              changedTables: [],
              sheetStatus: answer.state,
              statusRevision: answer.revision,
              statusChanged: answer.statusChanged ?? false,
            },
            { headers: { 'Content-Type': 'application/json', ETag: `"e-${answer.revision}"` } },
          ),
        );
      }
      if (url.pathname.endsWith('/workflow/history')) {
        return Promise.resolve(
          json([
            {
              action: 'Submit',
              approvedBySubmitter: false,
              at: '2026-11-02T10:00:00Z',
              byDisplayName: 'Alice Author',
              fromState: 'Draft',
              reason: null,
              sheetCode: 'S1',
              stepOrdinal: null,
              toState: 'Submitted',
            },
          ]),
        );
      }
      if (url.pathname === '/api/v1/documents/7') {
        summaryReads += 1;
        const body = { id: 7, projectId: 1, sheetStates: { S1: sheetState } };
        // The re-read after a transition waits for the test: actions must stay disabled until it lands.
        if (summaryReads === 1) return Promise.resolve(json(body));

        return new Promise<Response>((resolve) => {
          releaseSummary = () => resolve(json(body));
        });
      }

      return Promise.resolve(json({}));
    }),
  );
}

let client: QueryClient;

function Probe(): JSX.Element {
  const summary = useQuery({
    queryKey: ['document', 7, Period],
    queryFn: async () => (await fetch(`/api/v1/documents/7?periodKey=${String(Period)}`)).json() as Promise<unknown>,
  });
  const live = useSheetLiveRefresh({ documentId: 7, periodKey: Period, tables: Tables });
  const stale = useStatusStale(7, Period);

  return (
    <>
      {live.banner}
      <span data-testid="summary-ready">{summary.data === undefined ? 'no' : 'yes'}</span>
      <button type="button" data-testid="approve" aria-disabled={stale || undefined}>
        Approve
      </button>
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

const statusBanner = (): HTMLElement | null => document.querySelector('[data-testid="sheet-status-banner"]');
const approveDisabled = (): boolean => document.querySelector('[data-testid="approve"]')?.getAttribute('aria-disabled') === 'true';

beforeEach(() => {
  vi.useFakeTimers();
  answers = [];
  sheetState = 'Draft';
  releaseSummary = null;
  changeRequests.length = 0;
  summaryReads = 0;
  client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  install();
});

afterEach(() => {
  cleanup();
  client.clear();
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

describe('Live status (LIVE-STATUS)', () => {
  it('a new status revision re-reads the state, disables actions until it lands and shows a polite banner', async () => {
    answers = [{ revision: 'r1', state: 'Draft' }, { revision: 'r2', state: 'Submitted', statusChanged: true }];
    const invalidate = vi.spyOn(client, 'invalidateQueries');
    mount();
    await tick(10);
    expect(document.querySelector('[data-testid="summary-ready"]')?.textContent).toBe('yes');
    expect(statusBanner()).toBeNull();
    expect(approveDisabled()).toBe(false);

    // Someone else submits; the next poll carries the new revision.
    sheetState = 'Submitted';
    await tick(30_100);

    const keys = (): string[] => invalidate.mock.calls.map(([filters]) => JSON.stringify(filters?.queryKey));
    expect(keys()).toContain(JSON.stringify(['document', 7, Period]));
    // The summary is being re-read: the action against the old state is not offered.
    expect(approveDisabled()).toBe(true);
    expect(statusBanner()).toBeNull();

    await act(async () => {
      releaseSummary?.();
      await vi.advanceTimersByTimeAsync(10);
    });

    expect(approveDisabled()).toBe(false);
    // The same keys as own workflow actions (`refreshSheetWorkflow`): submission target too.
    expect(keys()).toContain(JSON.stringify(['submission-target', 7]));
    const banner = statusBanner();
    expect(banner?.getAttribute('data-live-status')).toBe('Submitted');
    expect(banner?.textContent).toContain(
      (Catalog['liveStatus.changedBy'] ?? '')
        .replace('{user}', 'Alice Author')
        .replace('{status}', Catalog['status.sheet.Submitted'] ?? ''),
    );
    // Polite live region (the same host as "Updated by").
    const host = document.querySelector('[data-testid="sheet-live-host"]');
    expect(host?.getAttribute('aria-live')).toBe('polite');
    expect(host?.contains(banner ?? null)).toBe(true);
  });

  it('an unchanged marker (304 on If-None-Match) does nothing', async () => {
    answers = [{ revision: 'r1', state: 'Draft' }, { status: 304, revision: 'r1', state: 'Draft' }];
    const invalidate = vi.spyOn(client, 'invalidateQueries');
    mount();
    await tick(10);
    await tick(30_100);

    expect(changeRequests).toHaveLength(2);
    expect(changeRequests[1]?.headers.get('If-None-Match')).toBe('"e-r1"');
    expect(new URL(changeRequests[1]?.url ?? '').searchParams.get('since')).toBe('m-r1');
    expect(invalidate).not.toHaveBeenCalled();
    expect(statusBanner()).toBeNull();
    expect(approveDisabled()).toBe(false);

    // The marker stays: the next poll asks with the same `since`.
    await tick(30_100);
    expect(new URL(changeRequests[2]?.url ?? '').searchParams.get('since')).toBe('m-r1');
  });

  /*
   * ✎ LIVE-STATUS-304 (e2e "a user who does not see the changed table": `200,200,304`): a run of `304`s is the normal
   * idle path — no error, no data banner, no status banner, the poll keeps going with the same `ETag` and still sees
   * the next real transition. Mutation: `isFinalChangesError` also stops on `304` (or `pollSheetChanges` via plain
   * `apiFetch`, no `If-None-Match`) → red.
   */
  it('a run of 304s keeps polling with the same ETag and still catches the next transition', async () => {
    answers = [
      { revision: 'r1', state: 'Draft' },
      { status: 304, revision: 'r1', state: 'Draft' },
      { status: 304, revision: 'r1', state: 'Draft' },
      { revision: 'r2', state: 'Submitted', statusChanged: true },
    ];
    const invalidate = vi.spyOn(client, 'invalidateQueries');
    mount();
    await tick(10);
    await tick(30_100);
    await tick(30_100);

    expect(changeRequests).toHaveLength(3);
    expect(changeRequests.slice(1).map((request) => request.headers.get('If-None-Match'))).toEqual(['"e-r1"', '"e-r1"']);
    expect(invalidate).not.toHaveBeenCalled();
    expect(statusBanner()).toBeNull();
    expect(document.querySelector('[data-testid="sheet-live-banner"]')).toBeNull();

    sheetState = 'Submitted';
    await tick(30_100);
    expect(changeRequests).toHaveLength(4);
    expect(invalidate).toHaveBeenCalled();
    await act(async () => {
      releaseSummary?.();
      await vi.advanceTimersByTimeAsync(10);
    });
    expect(statusBanner()?.getAttribute('data-live-status')).toBe('Submitted');
  });
});
