import { afterEach, describe, expect, it } from 'vitest';
import { setLanguage } from '@/shared/i18n';
import { formatDate, formatDateTime, formatNumber, formatTime } from '@/shared/format';

/**
 * Дати, час і числа — саме ЗНАЧЕННЯ, а не факт виклику.
 *
 * ⚠ Момент скрізь складається з ЛОКАЛЬНИХ складників (`new Date(2026, 8, 3,
 * 14, 5)`), а не з рядка ISO: `new Date('2026-09-03')` — це опівніч UTC, і на
 * схід від Гринвіча `formatDate` показав би четверте вересня. Тест став би
 * залежним від часового поясу машини — тобто зеленим тут і червоним у CI.
 */

/** Усі різновиди пробілу (`U+00A0`, `U+202F`) — до звичайного. */
function norm(value: string): string {
  return value.replace(/\s/g, ' ');
}

const At = new Date(2026, 8, 3, 14, 5);

afterEach(() => {
  localStorage.clear();
});

describe('formatDate', () => {
  it('ru: день, скорочений місяць, рік', () => {
    setLanguage('ru');

    expect(norm(formatDate(At))).toBe('3 сент. 2026 г.');
  });

  it('en: інший ПОРЯДОК складників, не лише інші слова', () => {
    setLanguage('en');

    expect(norm(formatDate(At))).toBe('Sep 3, 2026');
  });

  it('рядок ISO з сервера приймається нарівні з Date', () => {
    setLanguage('en');

    // ⚠ Друге твердження — не надмірність: саме лише порівняння двох викликів
    // між собою лишилося б зеленим і для функції, що завжди повертає порожньо.
    expect(formatDate(At.toISOString())).toBe(formatDate(At));
    expect(formatDate(At.toISOString())).not.toBe('');
  });

  it('опції місця виклику перекривають типові', () => {
    setLanguage('en');

    expect(norm(formatDate(At, { year: 'numeric', month: 'long', day: 'numeric' }))).toBe(
      'September 3, 2026',
    );
  });
});

describe('formatTime', () => {
  it('ru: 24 години', () => {
    setLanguage('ru');

    expect(norm(formatTime(At))).toBe('14:05');
  });

  it('en: 12 годин із позначкою половини доби', () => {
    setLanguage('en');

    expect(norm(formatTime(At))).toMatch(/^2:05\s?PM$/i);
  });
});

describe('formatDateTime', () => {
  it('ru: дата й час разом', () => {
    setLanguage('ru');

    expect(norm(formatDateTime(At))).toBe('3 сент. 2026 г., 14:05');
  });

  it('en: дата й час разом', () => {
    setLanguage('en');

    expect(norm(formatDateTime(At))).toMatch(/^Sep 3, 2026, 2:05\s?PM$/i);
  });
});

describe('formatNumber', () => {
  it('ru: пробіл на тисячі, кома на дріб', () => {
    setLanguage('ru');

    expect(norm(formatNumber(1234567.5))).toBe('1 234 567,5');
  });

  it('en: кома на тисячі, крапка на дріб', () => {
    setLanguage('en');

    expect(norm(formatNumber(1234567.5))).toBe('1,234,567.5');
  });

  it('опції місця виклику працюють (знаки після коми)', () => {
    setLanguage('en');

    expect(norm(formatNumber(1.5, { minimumFractionDigits: 3 }))).toBe('1.500');
  });

  it('нуль — це нуль, а не порожньо', () => {
    // ⛔ Найчастіша помилка в такій функції — `if (!value) return ''`: нуль
    // тоді зникає з комірки, і «викидів немає» стає «даних немає».
    setLanguage('en');

    expect(formatNumber(0)).toBe('0');
  });
});
