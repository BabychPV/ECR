import { afterEach, describe, expect, it, vi } from 'vitest';
import { QueryClient } from '@tanstack/react-query';
import { followImportJob } from '../followImportJob';

/** PERF-08 / PERF-16: the import follower backs off in the queue and does not ask from a hidden tab. */
function stubJobs(states: string[]): { asked: () => number } {
  const queue = [...states];
  let asked = 0;
  vi.stubGlobal(
    'fetch',
    vi.fn(() => {
      asked += 1;
      const state = queue.length > 1 ? queue.shift() : queue[0];

      return Promise.resolve(new Response(JSON.stringify({ state }), { status: 200, headers: { 'Content-Type': 'application/json' } }));
    }),
  );

  return { asked: () => asked };
}

const setVisibility = (state: 'visible' | 'hidden'): void => {
  Object.defineProperty(document, 'visibilityState', { configurable: true, get: () => state });
  document.dispatchEvent(new Event('visibilitychange'));
};

afterEach(() => {
  setVisibility('visible');
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('followImportJob poll rate', () => {
  it('queued: waits 2 -> 4 -> 8 -> 15 s (+-20 %); once Running the explicit beat applies', async () => {
    stubJobs(['Queued', 'Queued', 'Queued', 'Queued', 'Queued', 'Succeeded']);
    vi.spyOn(Math, 'random').mockReturnValue(0.5);
    const waits: number[] = [];

    await followImportJob(new QueryClient(), 'job-1', 1, 202609, { wait: (ms) => (waits.push(ms), Promise.resolve()) });

    expect(waits).toEqual([2000, 4000, 8000, 15000, 15000]);
  });

  it('a hidden tab asks nothing until the tab is visible again', async () => {
    const jobs = stubJobs(['Succeeded']);
    setVisibility('hidden');

    const done = followImportJob(new QueryClient(), 'job-1', 1, 202609, { wait: () => Promise.resolve() });
    await new Promise((resolve) => setTimeout(resolve, 20));
    expect(jobs.asked()).toBe(0);

    setVisibility('visible');
    await done;
    expect(jobs.asked()).toBe(1);
  });
});
