import { describe, expect, it } from 'vitest';
import { periodNote } from '@/features/projects/PeriodsOverview';
import { siteMomentText } from '@/shared/siteMoment';
import { deadlineText, summarizePeriodStates, type CalendarPeriod } from '@/shared/ui/periodStates';

/**
 * Приймальна RC8 №6, P2-1: дедлайн періоду розходився між екранами. Documents
 * («Open · closes in N days · дата», `PeriodPicker`) брала `graceEndsAt` —
 * НАЧАЛО пільги (`PeriodEnd + GraceOffsetDays`, `PeriodDto`), а Periods —
 * `endsAt`, жорстке закриття (`PeriodEnd + HardCloseOffsetDays`): вересень
 * «Oct 14» проти «Nov 13». Картка UI-13, приймання 4: підпис береться з
 * `endsAt`. Тут обидва екрани дають ту саму дату й те саме число днів.
 *
 * Вхідні — межі проєкту на `Asia/Atyrau` (+05:00) за політикою +14/+44.
 */
const Zone = 'Asia/Atyrau';
const Now = Date.parse('2026-10-07T09:00:00+05:00');

const september: CalendarPeriod = {
  periodKey: 202609,
  state: 'Open',
  graceEndsAt: '2026-10-15T00:00:00+05:00',
  endsAt: '2026-11-14T00:00:00+05:00',
};

const october: CalendarPeriod = {
  periodKey: 202610,
  state: 'Open',
  graceEndsAt: '2026-11-15T00:00:00+05:00',
  endsAt: '2026-12-15T00:00:00+05:00',
};

function documentsDeadline(period: CalendarPeriod): string {
  const summary = summarizePeriodStates([[period]], [Zone]).get(period.periodKey);
  if (summary === undefined) throw new Error('немає зведення');

  return deadlineText(summary, Now) ?? '';
}

function periodsNote(period: CalendarPeriod): string {
  const full = {
    ...period,
    startsAt: '2026-09-01T00:00:00+05:00',
    reopenedUntil: null,
    isCurrent: true,
  };
  const calendar = { timeZoneId: Zone, policy: { openOffsetDays: 0 }, periods: [full] };

  return periodNote(full as never, calendar as never, siteMomentText, Now);
}

describe('дедлайн періоду: Documents і Periods показують одне', () => {
  it('вересень: жорстке закриття — endsAt (13 листопада включно), а не початок пільги', () => {
    expect(documentsDeadline(september)).toContain('Nov 13, 2026');
    expect(periodsNote(september)).toContain('Nov 13, 2026');
  });

  it('жовтень: 14 грудня включно на обох екранах', () => {
    expect(documentsDeadline(october)).toContain('Dec 14, 2026');
    expect(periodsNote(october)).toContain('Dec 14, 2026');
  });

  it.each([september, october])('число днів збігається на обох екранах (%#)', (period) => {
    const count = (text: string): string | undefined => /count=(\d+)/.exec(text)?.[1];

    expect(count(documentsDeadline(period))).toBeDefined();
    expect(count(documentsDeadline(period))).toBe(count(periodsNote(period)));
  });

  it('вересень: 37 повних діб до 14.11 00:00 +05 (було 8 до початку пільги)', () => {
    expect(documentsDeadline(september)).toContain('count=37');
  });

  it('Grace: підпис теж за endsAt', () => {
    const grace: CalendarPeriod = { ...september, state: 'Grace' };

    expect(documentsDeadline(grace)).toContain('Nov 13, 2026');
  });

  it('проєкти розходяться: береться найраніше жорстке закриття', () => {
    const earlier: CalendarPeriod = { ...september, endsAt: '2026-11-01T00:00:00+05:00' };
    const summary = summarizePeriodStates([[september], [earlier]], [Zone, Zone]).get(202609);

    expect(summary?.closesAt).toBe(earlier.endsAt);
  });
});
