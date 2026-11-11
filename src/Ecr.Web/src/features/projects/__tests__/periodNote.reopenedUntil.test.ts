import { describe, expect, it } from 'vitest';
import { periodNote } from '@/features/projects/PeriodsOverview';
import { siteMomentText } from '@/shared/siteMoment';

/**
 * ✎ AN-178 (клас AN-169): плитка року Projects→Periods «reopened until {date} · edits are late». Строк перевідкриття
 * ВИКЛЮЧНИЙ — за замовчуванням північ майданчика, — і північ 12 листопада читалася «until Nov 12», на день пізніше, ніж
 * у смузі місяців («Nov 11, 11:59 PM»). Дата — останній день, коли ще можна.
 *
 * ⛔ Червоне без фіксу: `siteDate(reopenedUntil, zone, false)` → «Nov 12, 2026».
 */
const Zone = 'Asia/Atyrau';
const Now = Date.parse('2026-11-10T12:00:00+05:00');

/** Так сторінка Periods віддає `siteDate` у `PeriodsOverview` (дата без часу). */
const siteDate = (value: string, zone: string, inclusiveEnd: boolean): string | null =>
  siteMomentText(value, zone, true, inclusiveEnd);

const reopened = (reopenedUntil: string) => {
  const period = {
    periodKey: 202609,
    year: 2026,
    state: 'Open',
    startsAt: '2026-09-01T00:00:00+05:00',
    graceEndsAt: '2026-10-15T00:00:00+05:00',
    endsAt: '2026-10-31T00:00:00+05:00',
    reopenedUntil,
    isCurrent: false,
  };
  const calendar = { timeZoneId: Zone, policy: { openOffsetDays: 0 }, periods: [period] };

  return periodNote(period as never, calendar as never, siteDate, Now);
};

describe('плитка періоду: строк перевідкриття', () => {
  it('північ майданчика — останній день перед нею, не наступний', () => {
    const note = reopened('2026-11-12T00:00:00+05:00');

    expect(note).toContain('periods.tile.reopened');
    expect(note).toContain('Nov 11, 2026');
    expect(note).not.toContain('Nov 12, 2026');
  });

  it('строк посеред доби — той самий день', () => {
    expect(reopened('2026-11-12T15:00:00+05:00')).toContain('Nov 12, 2026');
  });
});
