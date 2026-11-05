import { describe, expect, it } from 'vitest';
import { DiscussionPollMs, PollJitterMs, SummaryPollMs, withJitter } from '../api';

/**
 * AN-155c: poll intervals of the table chat carry a random 0-3 s addition (five clients on the same beat gave p95
 * 1.9 s on the Land copy). The bounds are what keeps LIVE-REFRESH promises: a summary within 33 s, a chat within 13 s.
 */
describe('withJitter (AN-155c)', () => {
  it('adds 0..3 s to the base and never goes outside the window', () => {
    expect(PollJitterMs).toBe(3_000);
    expect(withJitter(SummaryPollMs, () => 0)).toBe(30_000);
    expect(withJitter(SummaryPollMs, () => 0.5)).toBe(31_500);
    expect(withJitter(DiscussionPollMs, () => 0.999_999_9)).toBeLessThan(DiscussionPollMs + PollJitterMs);
    // A broken random source cannot stretch or shrink the interval.
    expect(withJitter(DiscussionPollMs, () => 7)).toBeLessThan(DiscussionPollMs + PollJitterMs);
    expect(withJitter(DiscussionPollMs, () => -1)).toBe(DiscussionPollMs);
  });

  it('two clients with different random draws do not share the beat', () => {
    expect(withJitter(SummaryPollMs, () => 0.1)).not.toBe(withJitter(SummaryPollMs, () => 0.6));
  });
});
