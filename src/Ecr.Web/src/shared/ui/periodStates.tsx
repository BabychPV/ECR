import { useEffect, type JSX } from 'react';
import { useQueries } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type { PeriodCalendarDto } from '@/api/types';
import { Text } from '@mantine/core';
import { formatCount, formatDate } from '@/shared/format';
import { t } from '@/shared/i18n';

/** Стан періоду так, як його віддає календар проєкту (`PeriodDto.state`). */
export type PeriodStateName = 'Scheduled' | 'Open' | 'Grace' | 'Closed';

/** Найменше з календаря, що потрібне вибору періоду (UI-13). */
export interface CalendarPeriod {
  readonly periodKey: number;
  readonly state: string;
  readonly endsAt: string;
  readonly graceEndsAt: string | null;
}

/**
 * Стан одного `periodKey` у всіх завантажених календарях разом (UI-13).
 *
 * ⚠ На переліку документів проєктів кілька, і той самий місяць може бути
 * відкритий в одних і закритий в інших. `uniform` — так, коли стан однаковий
 * у всіх; інакше `state` — «найвідкритіший» (Open → Grace → Scheduled →
 * Closed), а `inState`/`total` дають чесне «Open in 4 of 5 projects».
 */
export interface PeriodStateSummary {
  readonly state: PeriodStateName;
  readonly uniform: boolean;
  readonly inState: number;
  readonly total: number;
  /** Коли стан скінчиться (найраніше серед проєктів у цьому стані): Open — початок пільги, Grace — жорстке закриття. */
  readonly closesAt: string | null;
}

const Precedence: readonly PeriodStateName[] = ['Open', 'Grace', 'Scheduled', 'Closed'];

function isStateName(value: string): value is PeriodStateName {
  return (Precedence as readonly string[]).includes(value);
}

/** Зведення станів за `periodKey` з кількох календарів. Стан, якого клієнт не знає, пропускається. */
export function summarizePeriodStates(
  calendars: readonly (readonly CalendarPeriod[])[],
): ReadonlyMap<number, PeriodStateSummary> {
  const byKey = new Map<number, CalendarPeriod[]>();

  for (const periods of calendars) {
    for (const period of periods) {
      if (!isStateName(period.state)) continue;
      const list = byKey.get(period.periodKey) ?? [];
      list.push(period);
      byKey.set(period.periodKey, list);
    }
  }

  const result = new Map<number, PeriodStateSummary>();

  for (const [key, periods] of byKey) {
    const state = Precedence.find((name) => periods.some((period) => period.state === name)) ?? 'Closed';
    const inState = periods.filter((period) => period.state === state);
    const ends = inState
      .map((period) => (state === 'Open' ? (period.graceEndsAt ?? period.endsAt) : period.endsAt))
      .filter((value) => !Number.isNaN(Date.parse(value)))
      .sort((left, right) => Date.parse(left) - Date.parse(right));

    result.set(key, {
      state,
      uniform: inState.length === periods.length,
      inState: inState.length,
      total: periods.length,
      closesAt: state === 'Open' || state === 'Grace' ? (ends[0] ?? null) : null,
    });
  }

  return result;
}

/**
 * Тягне календарі проєктів для `PeriodPicker` і віддає зведення нагору.
 *
 * ⚠ Окремий компонент, а не хук у самому `PeriodPicker`: вибір періоду живе
 * й там, де `QueryClientProvider` немає (форми в тестах, діалоги), а хук
 * запиту поза ним падає. Монтується лише тоді, коли викликач назвав проєкти.
 *
 * ⚠ Ключ запиту — той самий `['periods', id]`, що й в автовиборі періоду на
 * переліку документів і на сторінці документа: календар тягнеться один раз.
 */
export function PeriodStatesLoader({
  projectIds,
  onChange,
}: {
  projectIds: readonly number[];
  onChange: (states: ReadonlyMap<number, PeriodStateSummary> | undefined) => void;
}): JSX.Element | null {
  const calendars = useQueries({
    queries: projectIds.map((id) => ({
      queryKey: ['periods', id],
      queryFn: () => apiFetch<PeriodCalendarDto>(`/api/v1/projects/${String(id)}/periods`),
    })),
  });

  const ready = calendars.length > 0 && calendars.every((calendar) => calendar.data !== undefined);
  // ⚠ Залежність — відбиток даних, а не масив `useQueries` (він новий щоразу).
  const stamp = ready ? calendars.map((calendar) => calendar.dataUpdatedAt).join(',') : '';

  useEffect(() => {
    onChange(
      ready ? summarizePeriodStates(calendars.map((calendar) => calendar.data?.periods ?? [])) : undefined,
    );
    // ⚠ Лише `stamp`: він описує `calendars` повністю, а `onChange` — сеттер стану.
  }, [stamp]);

  return null;
}

/** Назва стану мовою інтерфейсу — ті самі рядки, що й у `StatusBadge` періоду. */
export function periodStateLabel(state: PeriodStateName): string {
  return state === 'Open'
    ? t('status.period.Open')
    : state === 'Grace'
      ? t('status.period.Grace')
      : state === 'Scheduled'
        ? t('status.period.Scheduled')
        : t('status.period.Closed');
}

/**
 * Стан для людини: «Open» або, коли проєкти розходяться, «Open in 4 of 5
 * projects» (UI-13: судження про перелік із кількох проєктів).
 */
export function periodStateText(summary: PeriodStateSummary): string {
  const label = periodStateLabel(summary.state);

  return summary.uniform
    ? label
    : t('period.stateInProjects', { state: label, count: summary.inState, total: summary.total });
}

/** Значок стану (макет: Open — коло, Grace і «ще не відкритий» — годинник, Closed — замок). */
export function PeriodStateIcon({ state }: { state: PeriodStateName }): JSX.Element {
  return (
    <svg width={12} height={12} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2} aria-hidden="true">
      {state === 'Open' ? (
        <circle cx={12} cy={12} r={8} />
      ) : state === 'Closed' ? (
        <>
          <rect x={5} y={11} width={14} height={10} rx={2} />
          <path d="M8 11V7a4 4 0 0 1 8 0v4" />
        </>
      ) : (
        <>
          <circle cx={12} cy={12} r={9} />
          <path d="M12 7v5l3 2" />
        </>
      )}
    </svg>
  );
}

/** Чип стану всередині контрола (макет `.period-state`): «○ Open». */
export function PeriodStateChip({ summary }: { summary: PeriodStateSummary }): JSX.Element {
  return (
    <span
      className="ecr-period-state"
      data-tone={summary.state === 'Grace' ? 'warning' : undefined}
      data-testid="period-state"
    >
      <PeriodStateIcon state={summary.state} />
      {periodStateLabel(summary.state)}
    </span>
  );
}

const DayMs = 24 * 60 * 60 * 1000;

/**
 * «Open · closes in 12 days · 30 Sep 2026» (макет `10-docs-list-light.png`).
 * Лише для відкритого чи пільгового періоду й лише з датою в майбутньому.
 */
export function deadlineText(summary: PeriodStateSummary, now: number = Date.now()): string | null {
  if (summary.closesAt === null) return null;

  const at = Date.parse(summary.closesAt);
  if (Number.isNaN(at) || at <= now) return null;

  const days = Math.ceil((at - now) / DayMs);

  return [
    periodStateText(summary),
    formatCount(days, 'period.closesIn'),
    formatDate(summary.closesAt, { day: 'numeric', month: 'short', year: 'numeric' }),
  ].join(' · ');
}

/** Підпис поруч із контролом; без дати закриття — нічого (`D15-06`). */
export function PeriodDeadline({ summary }: { summary: PeriodStateSummary }): JSX.Element | null {
  const text = deadlineText(summary);

  return text === null ? null : (
    <Text size="sm" c="dimmed" data-testid="period-deadline">
      {text}
    </Text>
  );
}
