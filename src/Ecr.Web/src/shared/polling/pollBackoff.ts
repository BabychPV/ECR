/*
 * PERF-08 (AUDIT-PERF1000): polling helpers shared by every job/status poller.
 *
 * Three rules live here, once, so that no screen invents its own variant:
 *  - an interval is spread by ±20 % (`jittered`), so 1000 clients that started together do not stay in step;
 *  - a poll that keeps getting the same answer backs off along a fixed schedule (`stepOf`), and snaps back to the
 *    first (fast) step as soon as the answer changes (`streakOf`);
 *  - the spread is DETERMINISTIC for one answer (a hash of a per-page salt and a key): TanStack evaluates
 *    `refetchInterval` several times per answer (each render), and a fresh random number per call would restart
 *    the interval timer every time.
 */

/** Share of the interval added or removed at random (±20 %). */
export const JitterFraction = 0.2;

/** Per page load: tabs opened together draw different spreads. */
const salt = Math.random();

/** A number in [0, 1) that depends only on (`salt`, `key`). */
function unit(key: number, seed: number): number {
  let h = Math.imul(Math.floor(key) ^ Math.floor(seed * 0x1_0000_0000), 0x9e37_79b1) >>> 0;
  h ^= h >>> 15;
  h = Math.imul(h, 0x85eb_ca6b) >>> 0;
  h ^= h >>> 13;

  return (h >>> 0) / 0x1_0000_0000;
}

/**
 * `ms` spread by ±`fraction`; the same `key` gives the same result (see the module comment).
 *
 * @param key Anything that changes once per poll answer (`dataUpdatedAt`, a streak counter).
 * @param seed Injectable for tests; defaults to the per-page salt.
 */
export function jittered(ms: number, key: number, fraction: number = JitterFraction, seed: number = salt): number {
  return Math.round(ms * (1 - fraction + 2 * fraction * unit(key, seed)));
}

/** The step of a schedule for the n-th consecutive unchanged answer (1-based); the last step repeats. */
export function stepOf(steps: readonly number[], streak: number): number {
  const last = steps.length - 1;

  return steps[Math.min(Math.max(streak - 1, 0), last)] ?? steps[last] ?? 0;
}

/** Waiting job (queue): 2 s, then 4, 8 and at most ~15 s between asks. */
export const QueuedPollSteps: readonly number[] = [2_000, 4_000, 8_000, 15_000];

interface StreakEntry {
  updatedAt: number;
  streak: number;
  signature: string;
}

const streaks = new WeakMap<object, StreakEntry>();

/**
 * How many consecutive answers of ONE query matched `signature` (1 = the first one, or the answer just changed).
 *
 * ⚠ Idempotent per answer: called again for the same `dataUpdatedAt` it returns the same number, so repeated
 * `refetchInterval` evaluations do not push the schedule forward.
 * ⚠ `signature === null` — the condition does not hold (e.g. the job is not queued): the streak resets to 0.
 */
export function streakOf(query: object, dataUpdatedAt: number, signature: string | null): number {
  const entry = streaks.get(query);
  if (entry !== undefined && entry.updatedAt === dataUpdatedAt) return entry.streak;

  const streak = signature === null ? 0 : entry !== undefined && entry.signature === signature ? entry.streak + 1 : 1;
  streaks.set(query, { updatedAt: dataUpdatedAt, streak, signature: signature ?? '' });

  return streak;
}

/** Exponential back-off for failures: `base`, 2×, 4×… up to `cap`. */
export function failureBackoffMs(base: number, failures: number, cap: number): number {
  return Math.min(cap, base * 2 ** Math.max(failures, 0));
}

/** Active list that keeps answering the same: 3 s, then 5, 8, 12 and at most 15 s. A change returns to 3 s. */
export const UnchangedPollSteps: readonly number[] = [3_000, 5_000, 8_000, 12_000, 15_000];

/**
 * Interval of a list poll that is active while `signature !== null`: backs off along `steps` while the answer
 * (its `signature`) does not change, snaps back to the first step when it does, +-20 % spread.
 */
export function unchangedBackoffMs(
  query: object,
  dataUpdatedAt: number,
  signature: string,
  steps: readonly number[] = UnchangedPollSteps,
): number {
  return jittered(stepOf(steps, streakOf(query, dataUpdatedAt, signature)), dataUpdatedAt);
}

/**
 * `ms` spread by ±`fraction` with a FRESH random number on each call - for timers the code owns (a chained
 * `setTimeout`), where every delay is drawn once. Not for `refetchInterval` (see `jittered`).
 */
export function randomJittered(ms: number, fraction: number = JitterFraction, random: () => number = Math.random): number {
  return Math.round(ms * (1 - fraction + 2 * fraction * Math.min(Math.max(random(), 0), 0.999_999)));
}
