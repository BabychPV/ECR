import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { testTheme } from '@/test/render';
import { CalcStatusChip, CalcStatusPollMs } from '../CalcStatusChip';
import { calcStatusOf, type CalcStatusSource, type LocalRecalc } from '../calcStatusModel';

/**
 * ✎ DOCUX L4 (§3.2 плану, Q2/Q3): один чип стану розрахунку; стан — із картки (сервер), тож переживає перезавантаження
 * (L4-1); збій лишається до Retry (L4-2); правка під час прогону — одразу «Out of date» (L4-3); закриті / затверджені
 * застарілі місяці — сіра довідка, не попередження (Q3).
 */
const Idle: LocalRecalc = { running: false, queued: false, failed: null };

function show(source: CalcStatusSource | undefined, local: LocalRecalc = Idle, run = vi.fn()): { client: QueryClient; run: typeof run } {
  const client = new QueryClient();
  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <CalcStatusChip documentId={96} periodKey={202601} source={source} local={local} recalculate={{ run, loading: false, blockedReason: null }} />
      </QueryClientProvider>
    </MantineProvider>,
  );
  return { client, run };
}

const kind = (): string | null => screen.queryByTestId('calc-status')?.getAttribute('data-calc-status') ?? null;

afterEach(() => vi.useRealTimers());

describe('calcStatusOf', () => {
  it('пріоритет: сервер рахує > у черзі > локальний збій > збій із картки > застаріло > актуально > невідомо', () => {
    expect(calcStatusOf({ activeRecalculation: { jobId: 'j', state: 'Running', stepDone: 3, stepTotal: 9, stepPeriodKey: 202603 } }, Idle).status).toEqual({
      kind: 'running',
      done: 3,
      total: 9,
      periodKey: 202603,
    });
    expect(calcStatusOf({ activeRecalculation: { jobId: 'j', state: 'Queued', queuePosition: 3 } }, Idle).status).toMatchObject({ kind: 'queued', position: 3 });
    expect(calcStatusOf({ resultsStale: true }, { ...Idle, failed: { errorCode: 'ECR-CALC-0512', correlationId: 'c' } }).status.kind).toBe('failed');
    expect(calcStatusOf({ resultsStale: true, lastRecalculation: { state: 'Failed', errorCode: 'E' } }, Idle).status.kind).toBe('failed');
    expect(calcStatusOf({ resultsStale: true }, Idle).status.kind).toBe('stale');
    expect(calcStatusOf({ resultsStale: false, lastRecalculation: { state: 'Succeeded', finishedAt: '2026-11-26T14:02:00Z' } }, Idle).status.kind).toBe('upToDate');
    expect(calcStatusOf(undefined, Idle).status.kind).toBe('unknown');
  });

  it('Q3: застарілі лише закриті / затверджені місяці — НЕ «застаріло», а довідка; відкритий застарілий — «застаріло»', () => {
    const locked = calcStatusOf(
      {
        resultsStale: false,
        staleMonths: [
          { periodKey: 202601, recalculable: false, lockedReason: 'closed' },
          { periodKey: 202602, recalculable: false, lockedReason: 'approved' },
        ],
      },
      Idle,
    );
    expect(locked.status.kind).toBe('upToDate');
    expect(locked.lockedMonths).toEqual([
      { periodKey: 202601, reason: 'closed' },
      { periodKey: 202602, reason: 'approved' },
    ]);
    expect(calcStatusOf({ resultsStale: true, staleMonths: [{ periodKey: 202610, recalculable: true }] }, Idle).status.kind).toBe('stale');
  });
});

describe('CalcStatusChip', () => {
  it('застаріло — жовтий чип «Results out of date» і «Recalculate» (F9) поруч; клік запускає ту саму дію', () => {
    const { run } = show({ resultsStale: true, resultsStaleSince: '2026-11-26T10:00:00Z' });
    expect(kind()).toBe('stale');
    expect(screen.getByTestId('calc-status-chip').textContent).toContain('calcStatus.stale');
    fireEvent.click(screen.getByTestId('calc-status-action'));
    expect(run).toHaveBeenCalledTimes(1);
    expect(screen.getByTestId('calc-status-action').getAttribute('aria-keyshortcuts')).toBe('F9');
  });

  it('L4-2: збій — червоний чип лишається, Retry ставить нову задачу, у поясненні — код і кореляція', () => {
    const { run } = show({ resultsStale: true }, { ...Idle, failed: { errorCode: 'ECR-CALC-0512', correlationId: '7f3a' } });
    expect(kind()).toBe('failed');
    expect(screen.getByTestId('calc-status-chip').textContent).toContain('ECR-CALC-0512 · 7f3a');
    expect(screen.getByTestId('calc-status-action').textContent).toContain('calcStatus.retry');
    fireEvent.click(screen.getByTestId('calc-status-action'));
    expect(run).toHaveBeenCalledTimes(1);
  });

  it('L4-1: задача рахується (із сервера) — картка перечитується кожні 3 с, поки вона йде; актуально — дії немає', () => {
    vi.useFakeTimers();
    const { client } = show({ resultsStale: true, activeRecalculation: { jobId: 'j', state: 'Running', stepDone: 3, stepTotal: 9, stepPeriodKey: 202603 } });
    const spy = vi.spyOn(client, 'invalidateQueries');
    expect(kind()).toBe('running');
    expect(screen.getByTestId('calc-status-chip').textContent).toContain('calcStatus.running');
    act(() => {
      vi.advanceTimersByTime(CalcStatusPollMs * 2 + 10);
    });
    expect(spy).toHaveBeenCalledTimes(2);
    expect(spy.mock.calls[0]?.[0]).toMatchObject({ queryKey: ['document', 96, 202601], exact: true });
    expect(screen.queryByTestId('calc-status-action')).toBeNull();
  });

  it('Q3: актуально + закриті місяці — сірий «✓ Calculated» і довідка «Jan: closed — not recalculated», без дії', () => {
    show({ resultsStale: false, staleMonths: [{ periodKey: 202601, recalculable: false, lockedReason: 'closed' }] });
    expect(kind()).toBe('upToDate');
    expect(screen.getByTestId('calc-status-locked').textContent).toContain('calcStatus.lockedMonths');
    expect(screen.queryByTestId('calc-status-action')).toBeNull();
  });

  it('стан невідомий і довідки немає — чипа немає', () => {
    show(undefined);
    expect(screen.queryByTestId('calc-status')).toBeNull();
  });
});
