/**
 * ✎ DOCUX L4 (план `docs/build/UX-REVIEW-document-screen.md` §3.2): стан розрахунку документа — ОДИН чип у рядку стану
 * замість банера «Calculation results are out of date» (№5), Alert під сіткою (№26) і смуги перерахунку проєкту (№7).
 *
 * ⚠ ТИМЧАСОВИЙ типізований адаптер контракту картки документа (L3, lane doc-ux-server): `staleMonths`,
 * `activeRecalculation`, `lastRecalculation`. Доки згенерованих типів немає, поля читаються з картки як НЕОБОВ'ЯЗКОВІ —
 * старий сервер їх не віддає, і чип працює з тим, що є (`resultsStale`, `resultsStaleSince`) плюс локальне стеження за
 * власною задачею (`SheetActions`). Після зведення — `CalcStatusSource` стає псевдонімом згенерованого `DocumentSummary`.
 */
export interface StaleMonthSource {
  readonly periodKey: number;
  readonly since?: string | null;
  readonly cause?: string | null;
  readonly registryName?: string | null;
  /** Можна перерахувати (місяць відкритий і не затверджений). */
  readonly recalculable: boolean;
  /** Чому не можна: `closed` | `approved` | … */
  readonly lockedReason?: string | null;
}

export interface ActiveRecalculationSource {
  readonly jobId: string;
  /** `Queued` | `Running` */
  readonly state: string;
  readonly queuePosition?: number | null;
  readonly estimatedStartAt?: string | null;
  readonly stepDone?: number | null;
  readonly stepTotal?: number | null;
  /** Місяць кроку, що рахується (`yyyyMM`). */
  readonly stepPeriodKey?: number | null;
  readonly startedBy?: string | null;
}

export interface LastRecalculationSource {
  readonly finishedAt?: string | null;
  /** `Succeeded` | `Failed` | `Partial` | … */
  readonly state: string;
  readonly errorCode?: string | null;
  readonly correlationId?: string | null;
}

export interface CalcStatusSource {
  readonly resultsStale?: boolean | null;
  readonly resultsStaleSince?: string | null;
  readonly staleMonths?: readonly StaleMonthSource[] | null;
  readonly activeRecalculation?: ActiveRecalculationSource | null;
  readonly lastRecalculation?: LastRecalculationSource | null;
}

/** Що знає вкладка про ВЛАСНУ задачу (до контракту — єдине джерело черги й збою). */
export interface LocalRecalc {
  readonly running: boolean;
  readonly queued: boolean;
  readonly failed: { readonly errorCode: string | null; readonly correlationId: string | null } | null;
}

export type CalcStatus =
  | { readonly kind: 'failed'; readonly errorCode: string | null; readonly correlationId: string | null }
  | { readonly kind: 'running'; readonly done: number | null; readonly total: number | null; readonly periodKey: number | null }
  | { readonly kind: 'queued'; readonly position: number | null; readonly estimatedStartAt: string | null }
  | { readonly kind: 'stale'; readonly since: string | null }
  | { readonly kind: 'upToDate'; readonly at: string | null }
  | { readonly kind: 'unknown' };

export interface CalcStatusView {
  readonly status: CalcStatus;
  /** Застарілі місяці, яких перерахувати не можна (закриті / затверджені) — СІРА довідка (Q3), не попередження. */
  readonly lockedMonths: readonly { readonly periodKey: number; readonly reason: 'closed' | 'approved' | 'other' }[];
}

function lockedReasonOf(reason: string | null | undefined): 'closed' | 'approved' | 'other' {
  const value = (reason ?? '').toLowerCase();
  if (value.includes('approv')) return 'approved';
  if (value.includes('clos')) return 'closed';
  return 'other';
}

/**
 * Стан чипа. Пріоритет: збій (доки не перераховано) → рахується → у черзі → застаріло → актуально → невідомо.
 * ⚠ Q3: «застаріло» — лише те, що МОЖНА перерахувати; закриті/затверджені місяці — довідка `lockedMonths`.
 * ⚠ ST-7: правки під час прогону — після кінця сервер каже `resultsStale = true`, і чип одразу «Out of date».
 */
export function calcStatusOf(source: CalcStatusSource | undefined, local: LocalRecalc): CalcStatusView {
  const months = source?.staleMonths ?? [];
  const lockedMonths = months
    .filter((month) => !month.recalculable)
    .map((month) => ({ periodKey: month.periodKey, reason: lockedReasonOf(month.lockedReason) }));
  const active = source?.activeRecalculation ?? null;
  const last = source?.lastRecalculation ?? null;

  const status: CalcStatus = (() => {
    if (active !== null) {
      return active.state === 'Queued'
        ? { kind: 'queued', position: active.queuePosition ?? null, estimatedStartAt: active.estimatedStartAt ?? null }
        : { kind: 'running', done: active.stepDone ?? null, total: active.stepTotal ?? null, periodKey: active.stepPeriodKey ?? null };
    }
    if (local.queued) return { kind: 'queued', position: null, estimatedStartAt: null };
    if (local.running) return { kind: 'running', done: null, total: null, periodKey: null };
    if (local.failed !== null) return { kind: 'failed', ...local.failed };
    if (last !== null && (last.state === 'Failed' || last.state === 'Partial') && source?.resultsStale !== false) {
      return { kind: 'failed', errorCode: last.errorCode ?? null, correlationId: last.correlationId ?? null };
    }
    const staleRecalculable = months.length > 0 ? months.some((month) => month.recalculable) : source?.resultsStale === true;
    if (staleRecalculable) return { kind: 'stale', since: source?.resultsStaleSince ?? null };
    if (source?.resultsStale === false) return { kind: 'upToDate', at: last?.finishedAt ?? null };
    return { kind: 'unknown' };
  })();

  return { status, lockedMonths };
}
