import type { components } from '@/api/schema';

/**
 * ✎ DOCUX L4 (план `docs/build/UX-REVIEW-document-screen.md` §3.2): стан розрахунку документа — ОДИН чип у рядку стану
 * замість банера «Calculation results are out of date» (№5), Alert під сіткою (№26) і смуги перерахунку проєкту (№7).
 *
 * Джерело — сервер (контракт doc-ux-server, L3): `GET /documents/{id}/calc-status` (`DocumentCalcStatusDto`): застарілі
 * місяці з «можна перерахувати» і причиною, задачі в черзі / в роботі (свої й чужі: авто, каскад, нічні), час останнього
 * розрахунку й останній провал. Поки відповіді немає (вантажиться, збій) — запас із картки (`resultsStale`) і власна задача
 * вкладки (`SheetActions`).
 */
export type DocumentCalcStatus = components['schemas']['DocumentCalcStatusDto'];
export type CalcActiveJob = components['schemas']['CalcActiveJobDto'];

/** Запас із картки документа (до відповіді `calc-status`). */
export interface CardStaleness {
  readonly resultsStale: boolean | null;
  readonly resultsStaleSince: string | null;
}

/** Що знає вкладка про ВЛАСНУ задачу (миттєвий відгук до відповіді сервера). */
export interface LocalRecalc {
  readonly running: boolean;
  readonly queued: boolean;
  readonly failed: { readonly errorCode: string | null; readonly correlationId: string | null } | null;
}

/** Чия задача: своя, чужа (з ім'ям), автоматична після правки, каскад від джерела, за розкладом. */
export interface JobOrigin {
  readonly kind: 'manual' | 'auto' | 'cascade' | 'scheduled' | 'other';
  readonly own: boolean;
  readonly by: string | null;
}

export type CalcStatus =
  | { readonly kind: 'failed'; readonly errorCode: string | null; readonly correlationId: string | null; readonly origin: JobOrigin | null }
  | {
      readonly kind: 'running';
      readonly done: number | null;
      readonly total: number | null;
      readonly periodKey: number | null;
      readonly percent: number | null;
      readonly origin: JobOrigin | null;
    }
  | { readonly kind: 'queued'; readonly position: number | null; readonly estimatedStartAt: string | null; readonly origin: JobOrigin | null }
  | { readonly kind: 'stale'; readonly since: string | null }
  | { readonly kind: 'upToDate'; readonly at: string | null }
  | { readonly kind: 'unknown' };

export type LockedReason = 'closed' | 'notOpen' | 'approved' | 'archived';

export interface CalcStatusView {
  readonly status: CalcStatus;
  /** Застарілі місяці, яких перерахувати не можна — СІРА довідка (Q3), не попередження. */
  readonly lockedMonths: readonly { readonly periodKey: number; readonly reason: LockedReason }[];
  /** Є задача, за якою треба стежити (опитування кожні 3 с). */
  readonly active: boolean;
}

function lockedReasonOf(reason: string | null): LockedReason {
  switch (reason) {
    case 'approved':
      return 'approved';
    case 'notOpen':
      return 'notOpen';
    case 'archived':
      return 'archived';
    default:
      return 'closed';
  }
}

function originOf(kind: string, own: boolean, by: string | null): JobOrigin {
  const known = kind === 'manual' || kind === 'auto' || kind === 'cascade' || kind === 'scheduled' ? kind : 'other';
  return { kind: known, own, by };
}

/** Яку задачу показати: та, що рахується, важливіша за чергу; своя — за чужу. */
function pickJob(jobs: readonly CalcActiveJob[]): CalcActiveJob | undefined {
  const rank = (job: CalcActiveJob): number => (job.state === 'Running' ? 0 : 2) + (job.own ? 0 : 1);
  return [...jobs].sort((a, b) => rank(a) - rank(b))[0];
}

/**
 * Стан чипа. Пріоритет: рахується → у черзі → збій (доки не перераховано) → застаріло → актуально → невідомо.
 * ⚠ Q3: «застаріло» — лише те, що МОЖНА перерахувати (`staleMonths[].recalculable`); решта — довідка `lockedMonths`.
 * ⚠ ST-7: правки під час прогону — після кінця сервер каже `resultsStale = true`, і чип одразу «Out of date».
 */
export function calcStatusOf(server: DocumentCalcStatus | undefined, card: CardStaleness, local: LocalRecalc): CalcStatusView {
  const months = server?.staleMonths ?? [];
  const lockedMonths = months
    .filter((month) => !month.recalculable)
    .map((month) => ({ periodKey: month.periodKey, reason: lockedReasonOf(month.lockedReason) }));
  const job = pickJob(server?.activeJobs ?? []);
  const resultsStale = server === undefined ? card.resultsStale : server.resultsStale;

  const status: CalcStatus = (() => {
    if (job !== undefined) {
      const origin = originOf(job.kind, job.own, job.startedByDisplayName);
      return job.state === 'Queued'
        ? { kind: 'queued', position: job.queuePosition, estimatedStartAt: job.estimatedStartAt, origin }
        : { kind: 'running', done: job.stepDone, total: job.stepTotal, periodKey: job.currentPeriodKey, percent: job.percent, origin };
    }
    if (local.queued) return { kind: 'queued', position: null, estimatedStartAt: null, origin: null };
    if (local.running) return { kind: 'running', done: null, total: null, periodKey: null, percent: null, origin: null };
    if (local.failed !== null) return { kind: 'failed', ...local.failed, origin: null };
    const failure = server?.lastFailure ?? null;
    if (failure !== null && resultsStale !== false) {
      return {
        kind: 'failed',
        errorCode: failure.errorCode,
        correlationId: failure.correlationId,
        origin: originOf(failure.kind, failure.own, null),
      };
    }
    const staleRecalculable = server === undefined ? card.resultsStale === true : months.some((month) => month.recalculable) || server.resultsStale === true;
    if (staleRecalculable) return { kind: 'stale', since: server?.resultsStaleSince ?? card.resultsStaleSince };
    if (resultsStale === false) return { kind: 'upToDate', at: server?.calculatedAt ?? null };
    return { kind: 'unknown' };
  })();

  return { status, lockedMonths, active: job !== undefined || local.queued || local.running };
}
