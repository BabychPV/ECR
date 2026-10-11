import { useEffect, type JSX } from 'react';
import { useQueries, useQueryClient, type UseQueryResult } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type { PeriodCalendarDto } from '@/api/types';
import { Text } from '@mantine/core';
import { formatCount } from '@/shared/format';
import { siteMomentText, wholeDaysUntil } from '@/shared/siteMoment';
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
  /**
   * Жорстке закриття (`endsAt`, виключна межа) — найраніше серед проєктів у цьому стані, для `Open` і `Grace`.
   *
   * ⛔ Не `graceEndsAt`: це початок пільги, а не закриття, і підпис Documents
   * розходився з Periods на місяць (P2-1, приймальна RC8 №6; картка UI-13).
   */
  readonly closesAt: string | null;
  /** Пояс проєкту, чия межа `closesAt`; у ньому й показується дата. */
  readonly closesZone: string;
}

const Precedence: readonly PeriodStateName[] = ['Open', 'Grace', 'Scheduled', 'Closed'];

function isStateName(value: string): value is PeriodStateName {
  return (Precedence as readonly string[]).includes(value);
}

/** Зведення станів за `periodKey` з кількох календарів. Стан, якого клієнт не знає, пропускається. */
export function summarizePeriodStates(
  calendars: readonly (readonly CalendarPeriod[])[],
  /** Пояс кожного календаря (`PeriodCalendarDto.timeZoneId`), в тому ж порядку; без нього — UTC. */
  zones: readonly string[] = [],
): ReadonlyMap<number, PeriodStateSummary> {
  const byKey = new Map<number, { period: CalendarPeriod; zone: string }[]>();

  calendars.forEach((periods, index) => {
    for (const period of periods) {
      if (!isStateName(period.state)) continue;
      const list = byKey.get(period.periodKey) ?? [];
      list.push({ period, zone: zones[index] ?? 'UTC' });
      byKey.set(period.periodKey, list);
    }
  });

  const result = new Map<number, PeriodStateSummary>();

  for (const [key, periods] of byKey) {
    const state = Precedence.find((name) => periods.some((entry) => entry.period.state === name)) ?? 'Closed';
    const inState = periods.filter((entry) => entry.period.state === state);
    const ends = inState
      .map((entry) => ({ at: entry.period.endsAt, zone: entry.zone }))
      .filter((entry) => !Number.isNaN(Date.parse(entry.at)))
      .sort((left, right) => Date.parse(left.at) - Date.parse(right.at));

    result.set(key, {
      state,
      uniform: inState.length === periods.length,
      inState: inState.length,
      total: periods.length,
      closesAt: state === 'Open' || state === 'Grace' ? (ends[0]?.at ?? null) : null,
      closesZone: ends[0]?.zone ?? 'UTC',
    });
  }

  return result;
}

/**
 * Скільки календарів проєктів летить у мережі одночасно (N4-08).
 *
 * ⛔ `useQueries` без межі на 500 проєктів ставив 500 запитів одразу: сервер і браузер (6 з'єднань на хост)
 * захлинались, а перші екрани чекали в черзі. Рядки НЕ відкидаються (огляд періодів має показувати всі
 * проєкти) — понад межу календарі просто стартують пізніше, у міру того як попередні завершаться.
 */
export const MaxConcurrentCalendars = 20;

/**
 * Календарі проєктів ключем `['periods', id]` з обмеженою одночасністю (N4-08).
 *
 * ⚠ Ключ спільний із календарем сторінки періодів і автовибором періоду в `DocumentsPage`: вже закешований
 * календар не займає «місця» в черзі. Результати йдуть у порядку `projectIds`.
 */
export function useProjectCalendars(projectIds: readonly number[]): UseQueryResult<PeriodCalendarDto>[] {
  const client = useQueryClient();
  let budget = MaxConcurrentCalendars;

  return useQueries({
    queries: projectIds.map((id) => {
      const status = client.getQueryState(['periods', id])?.status;
      // Завершений (дані чи помилка) не займає місця; решта — по черзі, поки вистачає бюджету.
      const settled = status === 'success' || status === 'error';
      const allowed = settled || budget > 0;
      if (!settled) budget -= 1;

      return {
        queryKey: ['periods', id],
        queryFn: () => apiFetch<PeriodCalendarDto>(`/api/v1/projects/${String(id)}/periods`),
        enabled: allowed,
      };
    }),
  });
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
 *
 * ⛔ N4-06: відмова календаря ОДНОГО проєкту не ховає зведення решти. Раніше «готово» вимагало дані в усіх
 * календарів, тож один збій назавжди лишав вибір періоду без станів і дедлайнів для всіх проєктів. Тепер
 * зведення — за календарями, що завантажились, щойно жоден не чекає відповіді; збійний у підсумок не входить.
 */
export function PeriodStatesLoader({
  projectIds,
  onChange,
}: {
  projectIds: readonly number[];
  onChange: (states: ReadonlyMap<number, PeriodStateSummary> | undefined) => void;
}): JSX.Element | null {
  const calendars = useProjectCalendars(projectIds);

  const loaded = calendars.filter((calendar) => calendar.data !== undefined);
  const ready = loaded.length > 0 && calendars.every((calendar) => calendar.status !== 'pending');
  // ⚠ Залежність — відбиток даних, а не масив `useQueries` (він новий щоразу).
  const stamp = ready ? loaded.map((calendar) => calendar.dataUpdatedAt).join(',') : '';

  useEffect(() => {
    onChange(
      ready
        ? summarizePeriodStates(
            loaded.map((calendar) => calendar.data?.periods ?? []),
            loaded.map((calendar) => calendar.data?.timeZoneId ?? 'UTC'),
          )
        : undefined,
    );
    // ⚠ Лише `stamp`: він описує `loaded` повністю, а `onChange` — сеттер стану.
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

/**
 * «Open · closes in 12 days · 30 Sep 2026» (макет `10-docs-list-light.png`).
 * Лише для відкритого чи пільгового періоду й лише з датою в майбутньому.
 *
 * ⚠ Дата й число днів — ті самі правила, що й на Periods (`wholeDaysUntil`,
 * `siteMomentText` з `inclusiveEnd`): останній день у поясі майданчика, повні
 * доби до жорсткого закриття (P2-1).
 */
export function deadlineText(summary: PeriodStateSummary, now: number = Date.now()): string | null {
  if (summary.closesAt === null) return null;

  const at = Date.parse(summary.closesAt);
  if (Number.isNaN(at) || at <= now) return null;

  const days = wholeDaysUntil(summary.closesAt, now) ?? 0;

  return [
    periodStateText(summary),
    days === 0 ? t('periods.tile.closesToday') : formatCount(days, 'period.closesIn'),
    siteMomentText(summary.closesAt, summary.closesZone, true, true) ?? summary.closesAt,
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
