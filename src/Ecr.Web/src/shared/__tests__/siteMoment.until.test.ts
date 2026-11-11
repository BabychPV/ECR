import { describe, expect, it } from 'vitest';
import { siteMomentText, siteUntilText } from '@/shared/siteMoment';

/** `Intl` вставляє вузькі нерозривні пробіли перед AM/PM — порівнюємо зі звичайними. */
const norm = (text: string | null): string | null => text?.replace(/[  ]/gu, ' ') ?? null;

/*
 * ✎ AN-169: строк «until …» на північ майданчика читався «until Nov 12, 12:00 AM» — двозначно (полудень? увесь 12-й?),
 * хоча правки закриваються на ПОЧАТКУ 12-го. `siteUntilText` показує північ останньою хвилиною попередньої доби.
 * ⛔ Червоне без фіксу: до нього всі виклики строку йшли через `siteMomentText` → «Nov 12, 2026, 12:00 AM».
 */
describe('siteUntilText — виключний строк «до …» у поясі майданчика', () => {
  it('північ майданчика зі зсувом у рядку — кінець попередньої доби, а не «12:00 AM» наступної', () => {
    const until = '2026-11-12T00:00:00+05:00';

    expect(norm(siteUntilText(until, 'Asia/Atyrau'))).toBe('Nov 11, 2026, 11:59 PM');
    expect(norm(siteMomentText(until, 'Asia/Atyrau'))).toBe('Nov 12, 2026, 12:00 AM');
  });

  it('північ майданчика в UTC-рядку (без настінного зсуву) — за поясом проєкту', () => {
    // 19:00Z = 00:00 12 листопада в Атирау (+05:00).
    expect(norm(siteUntilText('2026-11-11T19:00:00Z', 'Asia/Atyrau'))).toBe('Nov 11, 2026, 11:59 PM');
  });

  it('північ майданчика з дробовими нулями (.NET, 7 знаків) — теж кінець доби', () => {
    expect(norm(siteUntilText('2026-11-12T00:00:00.0000000+05:00', 'Asia/Atyrau'))).toBe('Nov 11, 2026, 11:59 PM');
  });

  it.each([
    ['2026-11-12T15:00:00+05:00', 'Nov 12, 2026, 3:00 PM'],
    ['2026-11-12T00:00:30+05:00', 'Nov 12, 2026, 12:00 AM'],
    ['2026-11-12T00:01:00+05:00', 'Nov 12, 2026, 12:01 AM'],
  ])('не північ (%s) — як є, той самий формат, що й siteMomentText', (until, shown) => {
    expect(norm(siteUntilText(until, 'Asia/Atyrau'))).toBe(shown);
    expect(siteUntilText(until, 'Asia/Atyrau')).toBe(siteMomentText(until, 'Asia/Atyrau'));
  });

  it('UTC-рядок, що є північчю браузера (UTC), але не майданчика, — не зсувається', () => {
    expect(norm(siteUntilText('2026-11-12T00:00:00Z', 'Asia/Atyrau'))).toBe('Nov 12, 2026, 5:00 AM');
  });

  it('нерозібраний рядок — null', () => {
    expect(siteUntilText('дурниця', 'Asia/Atyrau')).toBeNull();
  });
});
