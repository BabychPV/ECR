import { describe, it, expect } from 'vitest';
import type { JobSummary } from '@/api/types';
import { recentJobsUrl, myTasksUrl } from '@/features/jobs/api';
import { pollingJobCount } from '@/features/jobs/myTasks';
import { visibleRecalcs } from '@/features/recalc/ProjectRecalcBanner';
import { isProjectRecalculation, progressOf, RecalculationJobCode } from '@/features/recalc/recalcProgress';
import { projectRecalcUrl, rememberRecalcTotal, withExpectedTotal } from '@/features/recalc/useProjectRecalcProgress';

/**
 * ✎ UAT recalc-progress (08.11): стан перерахунку проєкту з рядка переліку задач.
 *
 * Мутації: (1) у `progressOf` не дивитись на `effectiveState` (`FannedOut` → «finished») — червоніє перший тест;
 * (2) у `pollingJobCount` прибрати `FannedOut` — червоніє «шухляда опитує»; (3) у `visibleRecalcs` показувати
 * закритий людиною — червоніє останній.
 */
function parent(overrides: Partial<JobSummary> = {}): JobSummary {
  return {
    jobId: 'IRecalculationJob~project7-year~1',
    jobCode: RecalculationJobCode,
    state: 'Succeeded',
    percent: 100,
    updatedAt: new Date().toISOString(),
    startedAt: new Date().toISOString(),
    documentId: null,
    projectId: 7,
    projectCode: 'LANDUI2026',
    effectiveState: 'FannedOut',
    fanOut: { total: 92, queued: 80, running: 4, succeeded: 7, failed: 1, skippedSheets: 3 },
    ...overrides,
  } as JobSummary;
}

describe('progressOf', () => {
  it('батько Succeeded із дітьми в роботі — running, не finished; лічильники з дітей', () => {
    const p = progressOf(parent());

    expect(p.phase).toBe('running');
    expect(p.total).toBe(92);
    expect(p.done).toBe(8);
    expect(p.failed).toBe(1);
    expect(p.skipped).toBe(3);
  });

  it('у черзі / розкладає — queued; провалений батько — failed; діти з помилками — finishedWithErrors', () => {
    expect(progressOf(parent({ state: 'Queued', effectiveState: null, fanOut: null })).phase).toBe('queued');
    expect(progressOf(parent({ state: 'Failed', effectiveState: null, fanOut: null })).phase).toBe('failed');
    expect(progressOf(parent({ effectiveState: 'SucceededWithErrors' })).phase).toBe('finishedWithErrors');
    expect(progressOf(parent({ effectiveState: 'Succeeded' })).phase).toBe('finished');
  });

  it('a1: поки батько ще розкладає (Running), часткове fanOut.total — не N; N — запам’ятане при постановці', () => {
    // ⛔ Живий стенд 09.11: батько Running, розкладено 1 з 3 документів → банер «Queued 1 documents…».
    const fanning = parent({
      jobId: 'IRecalculationJob~project9-year~1',
      state: 'Running',
      effectiveState: null,
      fanOut: { total: 1, queued: 1, running: 0, succeeded: 0, failed: 0, skippedSheets: 0 },
    });

    expect(progressOf(fanning).total).toBeNull();
    rememberRecalcTotal('IRecalculationJob~project9-year~1', 3);
    expect(withExpectedTotal(progressOf(fanning)).total).toBe(3);
  });

  it('документна дитина — не перерахунок проєкту', () => {
    expect(isProjectRecalculation(parent())).toBe(true);
    expect(isProjectRecalculation(parent({ documentId: 51 }))).toBe(false);
  });
});

describe('адреси й опитування', () => {
  it('банер питає власні перерахунки проєкту; шухляда — без дітей; перелік — за проєктом і батьком', () => {
    expect(projectRecalcUrl(7)).toBe('/api/v1/jobs?mine=true&kind=projectRecalculation&projectId=7&limit=1');
    expect(myTasksUrl()).toContain('hideChildren=true');
    expect(recentJobsUrl(true)).toBe('/api/v1/jobs?mine=true');
    expect(recentJobsUrl(false, { projectId: 7, parentJobId: 'a~b' })).toBe(
      '/api/v1/jobs?projectId=7&parentJobId=a%7Eb',
    );
  });

  it('шухляда опитує часто, поки діти перерахунку проєкту ще рахуються', () => {
    expect(pollingJobCount([parent()])).toBe(1);
    expect(pollingJobCount([parent({ effectiveState: 'Succeeded' })])).toBe(0);
  });
});

describe('visibleRecalcs', () => {
  it('йде — завжди; завершений — поки свіжий і не закритий людиною', () => {
    const now = Date.now();
    const running = progressOf(parent());
    const done = progressOf(parent({ jobId: 'done', effectiveState: 'Succeeded' }));
    const old = progressOf(
      parent({ jobId: 'old', effectiveState: 'Succeeded', updatedAt: new Date(now - 3 * 86_400_000).toISOString() }),
    );

    expect(visibleRecalcs([running, done, old], () => false, now).map((p) => p.jobId)).toEqual([running.jobId, 'done']);
    expect(visibleRecalcs([running, done], (id) => id === 'done', now).map((p) => p.jobId)).toEqual([running.jobId]);
  });
});
