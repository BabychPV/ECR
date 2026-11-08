import { describe, expect, it } from 'vitest';
import type { PeriodCalendarDto } from '@/api/types';
import {
  archiveBlock,
  calendarYears,
  deleteAllowed,
  lockMoment,
  monthList,
  nextLock,
  projectMonthsState,
  yearsWithProjects,
} from '@/features/projects/projectYear';

/**
 * ✎ Projects (макет 08.11): правила «проєкт = звітний рік» для переліку проєктів (f3), Lifecycle (c1–c3) і року
 * переліку документів (g1). Мутаційні докази (локально): кожен `expect` нижче червоніє, якщо в `projectYear.ts`
 * прибрати відповідну умову — названо біля перевірки.
 */

type Period = PeriodCalendarDto['periods'][number];

const p = (sequence: number, state: string, extra: { year?: number; endsAt?: string; reopenedUntil?: string | null } = {}): Period => ({
  id: sequence,
  periodKey: (extra.year ?? 2026) * 100 + sequence,
  year: extra.year ?? 2026,
  sequence,
  state,
  startsAt: '2026-01-01T00:00:00Z',
  endsAt: extra.endsAt ?? `2026-12-${String(sequence).padStart(2, '0')}T00:00:00Z`,
  graceEndsAt: '2026-12-01T00:00:00Z',
  reopenedUntil: extra.reopenedUntil ?? null,
  isCurrent: false,
  periodEnd: '2026-12-31',
}) as Period;

describe('projectYear', () => {
  it('g1: роки фільтра — лише роки з проєктами, від новішого; рік з адреси лишається', () => {
    const a = { periods: [p(1, 'Open'), p(2, 'Open')] };
    const b = { periods: [p(1, 'Closed', { year: 2024 })] };

    expect(yearsWithProjects([a, b, undefined], null)).toEqual([2026, 2024]);
    // ⛔ Мутація «без selected» губить рік з адреси.
    expect(yearsWithProjects([a], 2025)).toEqual([2026, 2025]);
    expect(calendarYears(undefined)).toEqual([]);
  });

  it('c3: архівація — лише активний і лише коли всі місяці заблоковані; невідомий календар — заборона', () => {
    const open = { periods: [p(9, 'Closed'), p(10, 'Open'), p(11, 'Grace'), p(12, 'Scheduled')] };

    expect(archiveBlock({ status: 'Archived' }, open)).toEqual({ kind: 'archived' });
    expect(archiveBlock({ status: 'Draft' }, open)).toEqual({ kind: 'notActive', status: 'Draft' });
    // ⛔ Мутація «undefined → дозволити» дає тут null.
    expect(archiveBlock({ status: 'Active' }, undefined)).toEqual({ kind: 'unknown' });
    // Названі місяці — ті, що приймають дані (Open/Grace), а не майбутні.
    const block = archiveBlock({ status: 'Active' }, open);
    expect(block?.kind === 'openMonths' ? block.months.map((m) => m.sequence) : []).toEqual([10, 11]);
    // Лише майбутні незаблоковані — названі вони.
    const future = archiveBlock({ status: 'Active' }, { periods: [p(1, 'Closed'), p(2, 'Scheduled')] });
    expect(future?.kind === 'openMonths' ? future.months.map((m) => m.sequence) : []).toEqual([2]);
    expect(archiveBlock({ status: 'Active' }, { periods: [p(1, 'Closed'), p(2, 'Closed')] })).toBeNull();
  });

  it('c1/c2: видалити — лише чернетку без документів; невідома кількість — заборона', () => {
    expect(deleteAllowed({ status: 'Draft' }, 0)).toBe(true);
    expect(deleteAllowed({ status: 'Draft' }, 3)).toBe(false);
    expect(deleteAllowed({ status: 'Draft' }, null)).toBe(false);
    expect(deleteAllowed({ status: 'Active' }, 0)).toBe(false);
  });

  it('f3: стан місяців у рядку переліку', () => {
    expect(projectMonthsState({ status: 'Draft' }, undefined)).toEqual({ kind: 'notStarted' });
    expect(projectMonthsState({ status: 'Active' }, undefined)).toEqual({ kind: 'unknown' });
    const open = projectMonthsState({ status: 'Active' }, { periods: [p(10, 'Open'), p(11, 'Grace'), p(12, 'Scheduled')] });
    expect(open.kind === 'open' ? monthList(open.months) : '').toBe('Oct, Nov 2026');
    expect(projectMonthsState({ status: 'Archived' }, { periods: [p(1, 'Closed')] })).toEqual({ kind: 'allLocked' });
  });

  it('f3: «Closes» — найраніше блокування серед місяців, що приймають дані; перевідкритий — за строком (D-68)', () => {
    const calendar = {
      periods: [
        p(9, 'Closed', { endsAt: '2026-01-01T00:00:00Z' }),
        p(10, 'Open', { endsAt: '2026-12-15T00:00:00Z' }),
        p(11, 'Open', { endsAt: '2026-12-20T00:00:00Z' }),
        p(3, 'Grace', { endsAt: '2026-04-01T00:00:00Z', reopenedUntil: '2026-12-25T00:00:00Z' }),
      ],
    };

    // ⛔ Мутація «без reopenedUntil» обирає березень за минулою датою 1 квітня — «closes» у минулому.
    expect(nextLock(calendar)?.sequence).toBe(10);
    expect(lockMoment(calendar.periods[3]!)).toBe('2026-12-25T00:00:00Z');
    expect(nextLock({ periods: [p(1, 'Closed')] })).toBeNull();
  });
});
