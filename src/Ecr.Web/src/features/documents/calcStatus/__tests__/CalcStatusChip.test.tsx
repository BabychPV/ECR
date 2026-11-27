import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { testTheme } from '@/test/render';
import { CalcStatusChip } from '../CalcStatusChip';
import { calcStatusOf, type CalcActiveJob, type CardStaleness, type DocumentCalcStatus, type LocalRecalc } from '../calcStatusModel';
import { CalcStatusPollMs } from '../useDocumentCalcStatus';

/**
 * ✎ DOCUX L4 (§3.2 плану, Q2/Q3; контракт doc-ux-server `GET /documents/{id}/calc-status`): один чип стану розрахунку.
 * Стан — із сервера, тож переживає перезавантаження і показує чужі й автоматичні задачі (L4-1); провал лишається до Retry
 * з кодом і кореляцією (L4-2); правка під час прогону — одразу «Out of date» (L4-3, `resultsStale` сервера); закриті /
 * затверджені застарілі місяці — сіра довідка, не попередження (Q3).
 */
const Idle: LocalRecalc = { running: false, queued: false, failed: null };
const NoCard: CardStaleness = { resultsStale: null, resultsStaleSince: null };

function dto(over: Partial<DocumentCalcStatus> = {}): DocumentCalcStatus {
  return {
    documentId: 96,
    periodKey: 202601,
    year: null,
    resultsStale: false,
    resultsStaleSince: null,
    resultsStaleUndetermined: null,
    staleMonths: [],
    activeJobs: [],
    calculatedAt: '2026-11-26T14:02:00Z',
    lastFailure: null,
    ...over,
  };
}

function job(over: Partial<CalcActiveJob> = {}): CalcActiveJob {
  return {
    jobId: 'j1',
    state: 'Running',
    kind: 'manual',
    percent: 30,
    queuePosition: null,
    estimatedStartAt: null,
    stepDone: 3,
    stepTotal: 9,
    currentPeriodKey: 202603,
    queuedAt: null,
    startedAt: null,
    own: true,
    startedByDisplayName: null,
    ...over,
  };
}

const json = (body: unknown): Response => new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });

function show(responses: DocumentCalcStatus[], local: LocalRecalc = Idle, run = vi.fn()): { run: typeof run; calls: () => number } {
  let index = 0;
  const fetchMock = vi.fn(async (_input: RequestInfo | URL) => json(responses[Math.min(index++, responses.length - 1)]));
  vi.stubGlobal('fetch', fetchMock);
  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <CalcStatusChip documentId={96} periodKey={202601} card={NoCard} local={local} recalculate={{ run, loading: false, blockedReason: null }} />
      </QueryClientProvider>
    </MantineProvider>,
  );
  return { run, calls: () => fetchMock.mock.calls.filter((call) => String(call[0]).includes('/calc-status')).length };
}

const kind = (): string | null => screen.queryByTestId('calc-status')?.getAttribute('data-calc-status') ?? null;

afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

describe('calcStatusOf', () => {
  it('пріоритет: рахується (своя важливіша за чужу) > у черзі > провал > застаріло > актуально > невідомо', () => {
    const running = calcStatusOf(dto({ activeJobs: [job({ state: 'Queued', queuePosition: 2 }), job({ jobId: 'j2', own: false, startedByDisplayName: 'Olena K.' })] }), NoCard, Idle);
    expect(running.status).toMatchObject({ kind: 'running', done: 3, total: 9, periodKey: 202603, origin: { own: false, by: 'Olena K.' } });
    expect(running.active).toBe(true);
    expect(calcStatusOf(dto({ activeJobs: [job({ state: 'Queued', queuePosition: 3, kind: 'auto' })] }), NoCard, Idle).status).toMatchObject({
      kind: 'queued',
      position: 3,
      origin: { kind: 'auto' },
    });
    expect(calcStatusOf(dto({ resultsStale: true, lastFailure: { jobId: 'f', failedAt: 'x', errorCode: 'ECR-SYS-0500', correlationId: 'c', kind: 'manual', own: true } }), NoCard, Idle).status.kind).toBe('failed');
    expect(calcStatusOf(dto({ resultsStale: true, staleMonths: [{ periodKey: 202610, since: null, recalculable: true, lockedReason: null }] }), NoCard, Idle).status.kind).toBe('stale');
    expect(calcStatusOf(dto(), NoCard, Idle).status).toEqual({ kind: 'upToDate', at: '2026-11-26T14:02:00Z' });
    expect(calcStatusOf(undefined, NoCard, Idle).status.kind).toBe('unknown');
    // До відповіді сервера — запас із картки.
    expect(calcStatusOf(undefined, { resultsStale: true, resultsStaleSince: null }, Idle).status.kind).toBe('stale');
  });

  it('Q3: застарілі лише закриті / затверджені / не відкриті місяці — «актуально» і довідка з причиною', () => {
    const view = calcStatusOf(
      dto({
        staleMonths: [
          { periodKey: 202601, since: null, recalculable: false, lockedReason: 'closed' },
          { periodKey: 202602, since: null, recalculable: false, lockedReason: 'approved' },
          { periodKey: 202612, since: null, recalculable: false, lockedReason: 'notOpen' },
        ],
      }),
      NoCard,
      Idle,
    );
    expect(view.status.kind).toBe('upToDate');
    expect(view.lockedMonths.map((month) => month.reason)).toEqual(['closed', 'approved', 'notOpen']);
  });
});

describe('CalcStatusChip', () => {
  it('застаріло — жовтий чип «Results out of date» і «Recalculate» (F9) поруч; клік запускає ту саму дію', async () => {
    const { run } = show([dto({ resultsStale: true, resultsStaleSince: '2026-11-26T10:00:00Z', staleMonths: [{ periodKey: 202601, since: null, recalculable: true, lockedReason: null }] })]);
    await waitFor(() => expect(kind()).toBe('stale'));
    fireEvent.click(screen.getByTestId('calc-status-action'));
    expect(run).toHaveBeenCalledTimes(1);
    expect(screen.getByTestId('calc-status-action').getAttribute('aria-keyshortcuts')).toBe('F9');
  });

  it('L4-2: провал із сервера — червоний чип, Retry, у поясненні код і кореляція для підтримки', async () => {
    const { run } = show([
      dto({ resultsStale: true, lastFailure: { jobId: 'f', failedAt: '2026-11-26T13:00:00Z', errorCode: 'ECR-SYS-0500', correlationId: '7f3a', kind: 'manual', own: true } }),
    ]);
    await waitFor(() => expect(kind()).toBe('failed'));
    expect(screen.getByTestId('calc-status-chip').textContent).toContain('ECR-SYS-0500 · 7f3a');
    fireEvent.click(screen.getByTestId('calc-status-action'));
    expect(run).toHaveBeenCalledTimes(1);
  });

  it('L4-1: задача (чужа, автоматична) рахується — чип каже хто й що, стан перечитується кожні 3 с до кінця; далі «Calculated»', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const { calls } = show([
      dto({ resultsStale: true, activeJobs: [job({ kind: 'auto', own: false, startedByDisplayName: 'Olena K.' })] }),
      dto({ resultsStale: true, activeJobs: [job({ stepDone: 7 })] }),
      dto(),
    ]);
    await waitFor(() => expect(kind()).toBe('running'));
    expect(screen.getByTestId('calc-status-chip').textContent).toContain('calcStatus.kind.auto');
    await act(async () => {
      await vi.advanceTimersByTimeAsync(CalcStatusPollMs * 2 + 50);
    });
    await waitFor(() => expect(kind()).toBe('upToDate'));
    expect(calls()).toBeGreaterThanOrEqual(3);
    expect(screen.queryByTestId('calc-status-action')).toBeNull();
  });

  it('чужа ручна задача — «started by …»', async () => {
    show([dto({ activeJobs: [job({ state: 'Queued', queuePosition: 2, own: false, startedByDisplayName: 'Ivan P.' })] })]);
    await waitFor(() => expect(kind()).toBe('queued'));
    expect(screen.getByTestId('calc-status-chip').textContent).toContain('calcStatus.startedBy (name=Ivan P.)');
  });

  it('Q3: актуально + закриті місяці — сірий «✓ Calculated» і довідка, без дії', async () => {
    show([dto({ staleMonths: [{ periodKey: 202601, since: null, recalculable: false, lockedReason: 'closed' }] })]);
    await waitFor(() => expect(kind()).toBe('upToDate'));
    expect(screen.getByTestId('calc-status-locked').textContent).toContain('calcStatus.lockedMonths');
    expect(screen.queryByTestId('calc-status-action')).toBeNull();
  });
});
