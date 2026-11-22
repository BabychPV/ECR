import { describe, expect, it } from 'vitest';
import { PollMs, jobRefetchInterval } from '../jobFollow';

/** PERF-08: a waiting job backs off, a running one stays live, a started one returns to fast. */
type Answer = { state?: string; effectiveState?: string | null } | undefined;

function ask(query: object, state: Answer, at: number): number | false {
  return jobRefetchInterval({ ...query, state: { data: state, dataUpdatedAt: at } } as never);
}

/** The same `query` object across answers, as TanStack keeps one Query per key. */
function session(): (state: Answer, at: number) => number | false {
  const query = {};
  const make = (state: Answer, at: number): number | false =>
    jobRefetchInterval(Object.assign(query, { state: { data: state, dataUpdatedAt: at } }) as never);

  return make;
}

const within = (value: number | false, base: number): void => {
  expect(value).not.toBe(false);
  expect(value as number).toBeGreaterThanOrEqual(base * 0.8);
  expect(value as number).toBeLessThanOrEqual(base * 1.2);
};

describe('jobRefetchInterval (PERF-08)', () => {
  it('queued: 2 -> 4 -> 8 -> 15 s, never above ~18 s with jitter', () => {
    const next = session();
    within(next({ state: 'Queued' }, 1), 2000);
    within(next({ state: 'Queued' }, 2), 4000);
    within(next({ state: 'Queued' }, 3), 8000);
    within(next({ state: 'Queued' }, 4), 15000);
    within(next({ state: 'Queued' }, 5), 15000);
  });

  it('running: stays live (~1.5 s +-20 %) and a start after a long wait resets to fast', () => {
    const next = session();
    for (let i = 1; i <= 6; i += 1) next({ state: 'Queued' }, i);
    within(next({ state: 'Running' }, 7), PollMs);
    within(next({ state: 'Running' }, 8), PollMs);
    // queued again later (a chain step): the schedule restarts from 2 s
    within(next({ state: 'Queued' }, 9), 2000);
  });

  it('repeated evaluation for the same answer returns the same value (no timer restarts)', () => {
    const next = session();
    const first = next({ state: 'Queued' }, 1);
    expect(next({ state: 'Queued' }, 1)).toBe(first);
    expect(next({ state: 'Queued' }, 1)).toBe(first);
  });

  it('stops on a terminal state and without an answer; keeps polling a fanned-out parent', () => {
    expect(ask({}, { state: 'Succeeded' }, 1)).toBe(false);
    expect(ask({}, { state: 'Failed' }, 1)).toBe(false);
    expect(ask({}, undefined, 1)).toBe(false);
    within(ask({}, { state: 'Succeeded', effectiveState: 'FannedOut' }, 1), PollMs);
  });

  it('honours a custom running interval (grid follower: 2 s)', () => {
    const query = {};
    within(jobRefetchInterval(Object.assign(query, { state: { data: { state: 'Running' }, dataUpdatedAt: 1 } }) as never, 2000), 2000);
  });
});
