import { afterEach, describe, expect, it, vi } from 'vitest';
import { DefaultLanguage, resetMissingReports, setLanguage } from '@/shared/i18n';
import { formatCount, formatDate, formatLocale, formatNumber, formatTime, pluralCategory } from '@/shared/format';

/**
 * Головне твердження модуля: локаль форматування — з мови ПРОДУКТУ.
 *
 * ⛔ «Локаль браузера» приходить ДВОМА різними каналами, і плутати їх не можна:
 *   1. Локаль СЕРЕДОВИЩА — те, що бере `Intl` без першого аргументу. Її задає
 *      ICU/ОС, і `navigator.language` до неї стосунку не має взагалі. У цьому
 *      прогоні вона `en-US` (перевіряється твердженням нижче, а не вважається).
 *   2. `navigator.language` — оголошена мова браузера. Її читає `i18n`, щоб
 *      ЗАПРОПОНУВАТИ мову при першому відкритті, але після вибору вона до
 *      форматування не має жодного стосунку.
 *
 * Тести нижче перекривають обидва канали окремо: інакше «правильний» результат
 * міг би вийти випадково — просто тому, що канал, який забули, збігся з
 * продуктовою мовою.
 */

/** Усі різновиди пробілу (`U+00A0`, `U+202F`) — до звичайного. */
function norm(value: string): string {
  return value.replace(/\s/g, ' ');
}

function withNavigatorLanguage(value: string): void {
  vi.stubGlobal('navigator', { language: value });
}

afterEach(() => {
  vi.unstubAllGlobals();
  localStorage.clear();

  // ⚠ `formatCount` нижче свідомо питає ключ, якого в каталозі немає: `t()`
  // ставить відкладену скаргу з таймером, і без скидання вона надрукувалася б
  // уже під час ЧУЖОГО тесту.
  resetMissingReports();
});

describe('локаль форматування — продукту, а не середовища', () => {
  it('середовище прогону справді НЕ російське (інакше твердження нижче порожнє)', () => {
    // ⚠ Це не «тест на тест». Без цієї перевірки наступне твердження було б
    // зелене і на коді, який локаль не передає взагалі, — якби ICU за
    // замовчуванням раптом виявився російським.
    expect(new Intl.NumberFormat().resolvedOptions().locale).not.toMatch(/^ru/);
  });

  it('продукт ru при англійському середовищі й англійському браузері дає російський формат', () => {
    withNavigatorLanguage('en-US');
    setLanguage('ru');

    // `en-US` дав би `1,234,567.5` — інший роздільник і тисяч, і дробу.
    expect(norm(formatNumber(1234567.5))).toBe('1 234 567,5');
  });

  it('продукт en при російському браузері дає англійський формат', () => {
    // ⛔ Дзеркальний бік: якби модуль брав `navigator.language`, тут вийшов би
    // російський формат. Перевірка саме в цей бік потрібна тому, що середовище
    // прогону англійське — і помилка «взяли локаль середовища» дала б у
    // попередньому тесті червоне, а тут зелене.
    withNavigatorLanguage('ru-RU');
    setLanguage('en');

    expect(norm(formatNumber(1234567.5))).toBe('1,234,567.5');
  });

  it('дата теж іде за продуктом, а не за середовищем', () => {
    withNavigatorLanguage('en-US');
    setLanguage('ru');

    // ⚠ Дата складена ЛОКАЛЬНИМИ складниками: `new Date('2026-09-03')` — це
    // опівніч UTC, і на схід від Гринвіча вона показала б четверте вересня.
    expect(norm(formatDate(new Date(2026, 8, 3)))).toBe('3 сент. 2026 г.');
  });

  it('12/24 години бере локаль, а не наш вибір', () => {
    const at = new Date(2026, 8, 3, 14, 5);

    setLanguage('ru');
    expect(norm(formatTime(at))).toBe('14:05');

    setLanguage('en');
    expect(norm(formatTime(at))).toMatch(/^2:05\s?PM$/i);
  });
});

describe("внутрішній код 'kz' стає тегом BCP-47", () => {
  it("formatLocale('kz') дає 'kk'", () => {
    expect(formatLocale('kz')).toBe('kk');
  });

  it("мапа не розходиться з i18n: тег збігається з тим, що i18n пише в <html lang>", () => {
    // ⛔ Це сторож проти ТИХОГО розходження двох копій відповідності
    // `kz → kk`. Друга копія живе в `shared/format/locale.ts` тому, що
    // `languageTag()` з i18n не експортовано, а чіпати i18n у цьому PR не
    // можна. Зв'язок між копіями тримається саме цим твердженням: i18n
    // публікує свій тег у `<html lang>`, і щойно одна з мап зміниться —
    // тут стане червоно.
    setLanguage('kz');

    expect(formatLocale()).toBe(document.documentElement.lang);
  });

  it("казахська отримує КАЗАХСЬКИЙ формат, а не англійський", () => {
    // ⛔ Саме тут ціна помилки, і вона мовчазна. `new Intl.NumberFormat('kz')`
    // НЕ кидає: `kz` синтаксично правильний тег, просто невідомий ICU, тож
    // `Intl` тихо підставляє локаль середовища (`en-US`). Без переведення
    // `kz → kk` казахський інтерфейс отримав би `1,234,567.5`.
    setLanguage('kz');

    expect(norm(formatNumber(1234567.5))).toBe('1 234 567,5');
  });
});

describe('непридатний тег мови не зносить екран', () => {
  /*
   * ⚠ Перелік навмисно змішаний, бо відмови РІЗНІ:
   *   • `xx-YY`, `qq` — синтаксично правильні, але ICU їх не знає: `Intl` НЕ
   *     кидає, а мовчки бере локаль середовища;
   *   • `''`, `'  '`, `en_US`, `*`, `zz-ZZ-ZZ` — синтаксично непридатні:
   *     `Intl` кидає `RangeError`.
   * Перша група небезпечніша: вона не лишає сліду.
   */
  const broken = ['xx-YY', 'qq', '', '  ', 'en_US', '*', 'zz-ZZ-ZZ'];

  it.each(broken)('тег %j не кидає з жодної функції форматування', (tag) => {
    setLanguage(tag);

    expect(() => formatNumber(1234.5)).not.toThrow();
    expect(() => formatDate(new Date(2026, 8, 3))).not.toThrow();
    expect(() => formatTime(new Date(2026, 8, 3, 14, 5))).not.toThrow();
    expect(() => pluralCategory(5)).not.toThrow();
    expect(() => formatCount(5, 'rows')).not.toThrow();
    expect(() => formatLocale()).not.toThrow();
  });

  it.each(broken)('тег %j відкочується на мову за замовчуванням продукту', (tag) => {
    setLanguage(tag);

    expect(formatLocale()).toBe(DefaultLanguage);
    expect(norm(formatNumber(1234567.5))).toBe('1,234,567.5');
  });

  it('відкат — саме мова продукту, а не локаль середовища', () => {
    // ⚠ У цьому прогоні обидві англійські, тому твердження вище саме по собі
    // їх не розрізняє. Розрізняє це: мова за замовчуванням продукту — `en`,
    // і `formatLocale()` віддає РІВНО її, а не `en-US` середовища.
    setLanguage('xx-YY');

    expect(formatLocale()).toBe('en');
    expect(formatLocale()).not.toBe(new Intl.NumberFormat().resolvedOptions().locale);
  });
});

describe('порожній вхід не вигадує значення', () => {
  it.each([null, undefined, 'не дата', Number.NaN])('дата %j дає порожньо', (value) => {
    setLanguage('en');

    expect(formatDate(value as never)).toBe('');
  });

  it('число null/undefined/NaN дає порожньо, а нескінченність — ні', () => {
    setLanguage('en');

    expect(formatNumber(null)).toBe('');
    expect(formatNumber(undefined)).toBe('');
    expect(formatNumber(Number.NaN)).toBe('');
    expect(formatNumber(Number.POSITIVE_INFINITY)).not.toBe('');
  });
});
