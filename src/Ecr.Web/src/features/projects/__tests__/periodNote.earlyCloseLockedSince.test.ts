import { describe, expect, it } from 'vitest';
import { periodNote } from '@/features/projects/PeriodsOverview';
import { siteMomentText } from '@/shared/siteMoment';

/**
 * ✎ debt-1109 F13 (P3 B): після ДОСТРОКОВОГО закриття перевідкриття плитка року на Projects→Periods казала
 * «Locked since Nov 10» — дату закриття винятку (`reopenedUntil`), а не межу блокування самого періоду (`endsAt`).
 *
 * Мутація (локально): повернути `reopenedUntil ?? endsAt` у `lockedSinceOf` — тест червоніє.
 */
const Zone = 'Asia/Atyrau';
const Now = Date.parse('2026-11-10T12:00:00+05:00');

const siteDate = (value: string, zone: string, inclusiveEnd: boolean): string | null =>
  siteMomentText(value, zone, true, inclusiveEnd);

describe('плитка періоду: «Locked since» після дострокового закриття перевідкриття', () => {
  it('показує межу блокування періоду, а не дату закриття перевідкриття', () => {
    const period = {
      periodKey: 202609,
      year: 2026,
      state: 'Closed',
      startsAt: '2026-09-01T00:00:00+05:00',
      graceEndsAt: '2026-10-15T00:00:00+05:00',
      endsAt: '2026-11-14T00:00:00+05:00',
      reopenedUntil: '2026-11-10T09:00:00+05:00',
      isCurrent: false,
    };
    const calendar = { timeZoneId: Zone, policy: { openOffsetDays: 0 }, periods: [period] };

    const note = periodNote(period as never, calendar as never, siteDate, Now);

    expect(note).toContain(siteDate(period.endsAt, Zone, false) ?? '');
    expect(note).not.toContain(siteDate(period.reopenedUntil, Zone, false) ?? '');
  });
});
