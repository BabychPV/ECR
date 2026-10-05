import { describe, expect, it } from 'vitest';
import { formatDateOnly, parseDateOnly, todayDateOnly } from '@/shared/format';

/**
 * L9-17 (AUDIT-2026-10-03): дата без часу ↔ `yyyy-MM-dd` — одне місце замість дев'яти копій.
 *
 * ⚠ Пояс у тесті задати не можна: пул `vmThreads` — це потоки, а присвоєння `process.env.TZ` у
 * потоці до рушія не доходить. Тому два виміри:
 *   • північ і пізній вечір ЛОКАЛЬНОЇ доби — у будь-якому поясі з ненульовим зсувом
 *     `toISOString()` ламає рівно один із них;
 *   • дата, чия локальна доба свідомо не збігається з UTC (підмінені складники), — ловить те саме
 *     і на машині в UTC (CI).
 *
 * Мутаційні докази (перевірено руками 2026-10-05, кожна — червоний тест):
 *   - `formatDateOnly` через `toISOString().slice(0, 10)` → 2 червоні (локальна доба, від'ємний пояс);
 *   - `parseDateOnly` без звірки складників → 2 червоні (`2026-02-30`, `2026-04-31`);
 *   - `parseDateOnly` через `new Date(y, m, d)` → «рік 0099 не стає 1999»;
 *   - рік без доповнення до 4 цифр → «доповнює місяць, день і рік нулями».
 */
describe('formatDateOnly', () => {
  it.each([0, 23])('бере локальну добу, година %i', (hour) => {
    expect(formatDateOnly(new Date(2026, 2, 1, hour, 30))).toBe('2026-03-01');
  });

  it('не читає UTC: локальна доба 2 березня за UTC-доби 1 березня', () => {
    const picked = new Date(Date.UTC(2026, 2, 1, 12));
    picked.getFullYear = () => 2026;
    picked.getMonth = () => 2;
    picked.getDate = () => 2;

    expect(formatDateOnly(picked)).toBe('2026-03-02');
  });

  it('від\'ємний пояс: вечір 31 грудня лишається 31 грудня, хоча в UTC уже новий рік', () => {
    const evening = new Date(Date.UTC(2027, 0, 1, 3));
    evening.getFullYear = () => 2026;
    evening.getMonth = () => 11;
    evening.getDate = () => 31;

    expect(formatDateOnly(evening)).toBe('2026-12-31');
  });

  it('доповнює місяць, день і рік нулями', () => {
    expect(formatDateOnly(new Date(2026, 0, 5))).toBe('2026-01-05');
    const early = new Date(2026, 0, 5);
    early.setFullYear(99);
    expect(formatDateOnly(early)).toBe('0099-01-05');
  });

  it('null → null', () => {
    expect(formatDateOnly(null)).toBeNull();
  });
});

describe('parseDateOnly', () => {
  it('рядок → МІСЦЕВА північ того самого дня', () => {
    const parsed = parseDateOnly('2026-03-01');

    expect(parsed).not.toBeNull();
    expect([parsed?.getFullYear(), parsed?.getMonth(), parsed?.getDate(), parsed?.getHours()]).toEqual([2026, 2, 1, 0]);
  });

  it('межа року: 31 грудня і 1 січня лишаються своїми днями', () => {
    expect(formatDateOnly(parseDateOnly('2026-12-31'))).toBe('2026-12-31');
    expect(formatDateOnly(parseDateOnly('2027-01-01'))).toBe('2027-01-01');
  });

  it('рік 0099 не стає 1999', () => {
    expect(parseDateOnly('0099-01-05')?.getFullYear()).toBe(99);
  });

  it.each([null, undefined, '', '2026-3-1', '2026-03-01T10:00:00', '01.03.2026', 'дурниця'])(
    'не той формат (%s) → null',
    (value) => {
      expect(parseDateOnly(value)).toBeNull();
    },
  );

  it.each(['2026-02-30', '2026-13-01', '2026-00-10', '2026-04-31'])('неіснуючий день %s → null, не сусідній', (value) => {
    expect(parseDateOnly(value)).toBeNull();
  });

  it('29 лютого високосного року — день є', () => {
    expect(formatDateOnly(parseDateOnly('2028-02-29'))).toBe('2028-02-29');
  });
});

describe('todayDateOnly', () => {
  it('бере календарний день клієнта, а не UTC: 00:30 і 23:30 — той самий день', () => {
    expect(todayDateOnly(new Date(2026, 8, 30, 0, 30))).toBe('2026-09-30');
    expect(todayDateOnly(new Date(2026, 8, 30, 23, 30))).toBe('2026-09-30');
  });

  it('без аргументу — сьогодні', () => {
    const now = new Date();

    expect(todayDateOnly()).toBe(formatDateOnly(now));
  });
});
