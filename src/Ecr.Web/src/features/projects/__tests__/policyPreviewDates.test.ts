import { describe, expect, it } from 'vitest';
import type { PeriodCalendarDto } from '@/api/types';
import {
  addDays,
  policyDates,
  previewPeriods,
  projectEndOf,
  samplePeriods,
  siteDateOf,
  yearEditsUntil,
} from '@/features/projects/policyPreviewDates';

/**
 * Приймання 08.11, f5: прев'ю дат політики рахує РІВНО як сервер.
 *
 * ⛔ Дзеркало `Period.RecomputeBoundaries` (Ecr.Domain): відкриття — `PeriodStart + OpenOffsetDays`, пізні правки —
 * `PeriodEnd + GraceOffsetDays`, закриття — `PeriodEnd + HardCloseOffsetDays` (без прихованого «+1», Q-337);
 * вікно року — `YearGraceWindow.For`: `31.12.2026 + 45 → 14.02.2027 включно` (приклад із коментаря сервера).
 */
const std = { openOffsetDays: 0, graceOffsetDays: 15, hardCloseOffsetDays: 45, yearGraceOffsetDays: 45 };

const calendar = {
  projectId: 5,
  periodKind: 'Monthly',
  currentPeriodMode: 'Auto',
  timeZoneId: 'Asia/Atyrau',
  policy: { id: 7, code: 'STD', openOffsetDays: -2, graceOffsetDays: 15, hardCloseOffsetDays: 45, yearGraceOffsetDays: 45 },
  periods: [
    // Порядок навмисно змішаний: календар сортується за ключем.
    { id: 11, periodKey: 202611, year: 2026, sequence: 11, state: 'Scheduled', isCurrent: false, periodEnd: '2026-11-30', startsAt: '2026-10-30T00:00:00+05:00', graceEndsAt: null, endsAt: '2027-01-14T00:00:00+05:00', reopenedUntil: null },
    { id: 9, periodKey: 202609, year: 2026, sequence: 9, state: 'Closed', isCurrent: false, periodEnd: '2026-09-30', startsAt: '2026-08-30T00:00:00+05:00', graceEndsAt: null, endsAt: '2026-11-14T00:00:00+05:00', reopenedUntil: null },
    { id: 10, periodKey: 202610, year: 2026, sequence: 10, state: 'Open', isCurrent: true, periodEnd: '2026-10-31', startsAt: '2026-09-29T00:00:00+05:00', graceEndsAt: null, endsAt: '2026-12-15T00:00:00+05:00', reopenedUntil: null },
    { id: 12, periodKey: 202612, year: 2026, sequence: 12, state: 'Scheduled', isCurrent: false, periodEnd: '2026-12-31', startsAt: '2026-11-29T00:00:00+05:00', graceEndsAt: null, endsAt: '2027-02-14T00:00:00+05:00', reopenedUntil: null },
  ],
} as unknown as PeriodCalendarDto;

describe('прев\'ю дат політики — серверне правило', () => {
  it('жовтень 2026, 0/15/45: відкрито 01.10, пізні правки з 15.11, закрито з 15.12', () => {
    expect(policyDates({ periodKey: 202610, periodStart: '2026-10-01', periodEnd: '2026-10-31' }, std)).toEqual({
      periodKey: 202610,
      opensOn: '2026-10-01',
      lateFrom: '2026-11-15',
      locksOn: '2026-12-15',
    });
  });

  it('від\'ємний зсув відкриття і перехід року', () => {
    const dates = policyDates(
      { periodKey: 202612, periodStart: '2026-12-01', periodEnd: '2026-12-31' },
      { ...std, openOffsetDays: -5, hardCloseOffsetDays: 60 },
    );

    expect(dates.opensOn).toBe('2026-11-26');
    expect(dates.locksOn).toBe('2027-03-01');
  });

  it('вікно року: 31.12.2026 + 45 → 14.02.2027 включно; від\'ємне — без вікна', () => {
    expect(yearEditsUntil('2026-12-31', std)).toBe('2027-02-14');
    expect(yearEditsUntil('2026-12-31', { ...std, yearGraceOffsetDays: -3 })).toBe('2026-12-31');
  });

  it('addDays — календарні доби, лютий високосного року', () => {
    expect(addDays('2028-02-28', 1)).toBe('2028-02-29');
    expect(addDays('2027-02-28', 1)).toBe('2027-03-01');
  });

  it('поточний (isCurrent) і наступний період; початок — день після кінця попереднього', () => {
    expect(previewPeriods(calendar)).toEqual([
      { periodKey: 202610, periodStart: '2026-10-01', periodEnd: '2026-10-31' },
      { periodKey: 202611, periodStart: '2026-11-01', periodEnd: '2026-11-30' },
    ]);
    expect(projectEndOf(calendar)).toBe('2026-12-31');
  });

  it('початок ПЕРШОГО періоду — дата startsAt за майданчиком мінус зсув відкриття чинної політики', () => {
    const first = {
      ...calendar,
      periods: [{ ...calendar.periods[1]!, isCurrent: true }],
    } as PeriodCalendarDto;

    expect(previewPeriods(first)).toEqual([{ periodKey: 202609, periodStart: '2026-09-01', periodEnd: '2026-09-30' }]);
  });

  it('startsAt у UTC (Z) читається поясом майданчика, а не браузера', () => {
    // 31.08 19:00 UTC = 01.09 00:00 у Asia/Atyrau (+05:00).
    expect(siteDateOf('2026-08-31T19:00:00Z', 'Asia/Atyrau')).toBe('2026-09-01');
  });

  it('зразок без проєкту: місяць сьогодні і наступний, грудень → січень наступного року', () => {
    expect(samplePeriods('2026-12-09')).toEqual({
      periods: [
        { periodKey: 202612, periodStart: '2026-12-01', periodEnd: '2026-12-31' },
        { periodKey: 202701, periodStart: '2027-01-01', periodEnd: '2027-01-31' },
      ],
      projectEnd: '2026-12-31',
    });
  });
});
