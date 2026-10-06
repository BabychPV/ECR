import { describe, expect, it } from 'vitest';
import { formatDateOnly } from '@/shared/format';
import { acceptsDottedDate, parseUserDate } from '@/shared/dates/userDate';

/**
 * A1-02: набрана людиною дата → календарний день, СТРОГО за мовою інтерфейсу.
 *
 * ⛔ Дефект, який сторожать ці тести (приймальний прохід A1, шапка документа): `05.10.2026`
 * зберігалось як 2026-05-10, `2026-13-45` — як 2027-02-14.
 */
function day(text: string, language: string): string | null {
  const parsed = parseUserDate(text, language);
  return parsed === null ? null : formatDateOnly(parsed);
}

describe('parseUserDate (A1-02)', () => {
  it.each(['ru', 'kz'])('%s: dd.MM.yyyy — день, потім місяць (5 жовтня, не 10 травня)', (language) => {
    expect(day('05.10.2026', language)).toBe('2026-10-05');
    expect(day('5.10.2026', language)).toBe('2026-10-05');
    expect(day('31.12.2026', language)).toBe('2026-12-31');
    expect(day(' 01.02.2026 ', language)).toBe('2026-02-01');
  });

  it.each(['en', 'ru', 'kz'])('%s: формат показу поля yyyy-MM-dd розбирається в будь-якій мові', (language) => {
    expect(day('2026-10-05', language)).toBe('2026-10-05');
    expect(day('2024-02-29', language)).toBe('2024-02-29');
  });

  it('en: dd.MM.yyyy неоднозначна (5 жовтня чи 10 травня) — відмова, а не вгадування', () => {
    expect(day('05.10.2026', 'en')).toBeNull();
    expect(day('05.10.2026', 'en-GB')).toBeNull();
    expect(acceptsDottedDate('en')).toBe(false);
    expect(acceptsDottedDate('kz')).toBe(true);
  });

  it.each(['en', 'ru', 'kz'])('%s: неіснуючий день — null, без перекочування', (language) => {
    expect(day('2026-13-45', language)).toBeNull();
    expect(day('2026-02-29', language)).toBeNull();
    expect(day('2026-04-31', language)).toBeNull();
    expect(day('2026-00-10', language)).toBeNull();
    expect(day('2026-10-00', language)).toBeNull();
    expect(day('0000-01-01', language)).toBeNull();
  });

  it.each(['ru', 'kz'])('%s: неіснуючий день у dd.MM.yyyy — null', (language) => {
    expect(day('45.13.2026', language)).toBeNull();
    expect(day('30.02.2026', language)).toBeNull();
    expect(day('31.04.2026', language)).toBeNull();
  });

  it.each(['en', 'ru', 'kz'])('%s: чужі й неповні форми — null (жодного розбору «як вийде»)', (language) => {
    for (const text of [
      '',
      '   ',
      '2026',
      '2026-10',
      '2026-1-5',
      '20261005',
      '05.10.26',
      '05/10/2026',
      '10/05/2026',
      'Oct 5, 2026',
      '2026-10-05T00:00:00',
      '2026-10-05Z',
      '05.10.2026.',
      'abc',
    ]) {
      expect(day(text, language), text).toBeNull();
    }
  });

  it('роки 1–99 не стають 1900-ми', () => {
    expect(day('0099-01-01', 'en')).toBe('0099-01-01');
    expect(day('01.01.0099', 'ru')).toBe('0099-01-01');
  });

  it('результат — північ місцевого часу', () => {
    const parsed = parseUserDate('05.10.2026', 'ru');
    expect(parsed?.getHours()).toBe(0);
    expect(parsed?.getMinutes()).toBe(0);
  });
});
