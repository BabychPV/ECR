import { describe, expect, it } from 'vitest';
import type { JobSummary } from '@/api/types';
import { MyTasksBackoffSteps, myTasksRefetchInterval } from '../useMyTasks';

/** PERF-08: My tasks beat backs off while nothing changes, resets on progress, and idles with a spread. */
function job(over: Partial<JobSummary> = {}): JobSummary {
  return { jobId: 'J#1', jobCode: 'Ecr.Application.Ports.IRecalculationJob', state: 'Queued', percent: 0, ...over } as JobSummary;
}

function session(): (data: JobSummary[], at: number) => number {
  const query = {};

  return (data, at) => myTasksRefetchInterval(Object.assign(query, { state: { data, dataUpdatedAt: at } }));
}

const within = (value: number, base: number): void => {
  expect(value).toBeGreaterThanOrEqual(base * 0.8);
  expect(value).toBeLessThanOrEqual(base * 1.2);
};

describe('myTasksRefetchInterval', () => {
  it('queued and unchanged: 3 s then backs off to the cap', () => {
    const next = session();
    MyTasksBackoffSteps.forEach((base, i) => within(next([job()], i + 1), base));
    within(next([job()], 99), 15_000);
  });

  it('progress resets to the fast beat', () => {
    const next = session();
    for (let i = 1; i <= 5; i += 1) next([job({ state: 'Running', percent: 10 })], i);
    within(next([job({ state: 'Running', percent: 20 })], 6), 3_000);
  });

  it('idle: 30 s with a spread, not a fixed beat', () => {
    const next = session();
    const values = new Set<number>();
    for (let i = 1; i <= 200; i += 1) {
      const value = next([], i);
      within(value, 30_000);
      values.add(value);
    }
    expect(values.size).toBeGreaterThan(50);
  });

  it('a formula recalculation does not count as activity (P2-05 kept)', () => {
    const next = session();
    const formula = job({ jobCode: 'Ecr.Application.Ports.IFormulaRecalculationJob', state: 'Running' });
    within(next([formula], 1), 30_000);
  });
});
