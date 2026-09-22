import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { DefaultLanguage, setLanguage } from '@/shared/i18n';
import { formatDecimal } from '../number';

/**
 * ПОДАННЯ десяткового, який приїхав РЯДКОМ (`e470777a`).
 *
 * ⛔ Порівняння значень уже має власний дім (`shared/format/decimal.ts`,
 * `d9e75a82`) і власні тести; тут перевіряється рівно те, чого там немає й не
 * має бути, — локаль, роздільники і те, що дорогою до екрана не з'явився
 * `Number`.
 */

/** Нерозривні пробіли ICU → звичайний: інакше падіння нечитабельне. */
function norm(text: string | null): string | null {
  return text === null ? null : text.replace(/[   ]/g, ' ');
}

/**
 * Значення, яке `double` ГАРАНТОВАНО псує, — і доказ цього поруч.
 *
 * ⛔ Не будь-які «16 знаків»: `String(Number('1.2345678901234567'))` повертає
 * той самий рядок, тобто мутація «пустити через `Number`» лишилася б ЗЕЛЕНОЮ.
 * Тут двадцять значущих цифр, і `Number` зрізає три останні — це перевіряється
 * першим же твердженням, а не вважається.
 */
const Twenty = '1234.1234567890123456';
const TwentyThroughDouble = '1234.1234567890124';

beforeEach(() => {
  setLanguage('en');
});

afterEach(() => {
  setLanguage(DefaultLanguage);
  localStorage.clear();
});

describe('замір середовища: що саме робить Number і що вміє Intl', () => {
  it('Number(...) на цьому вході ТАКИ псує значення', () => {
    // ⛔ Без цього твердження решта файлу доводила б менше, ніж обіцяє: мутація
    // «повернути `Number` на шлях показу» мусить ЩОСЬ ламати, і ось
    // підтвердження, що на цьому вході вона ламає.
    expect(String(Number(Twenty))).toBe(TwentyThroughDouble);
    expect(TwentyThroughDouble).not.toBe(Twenty);
  });

  it('Intl.NumberFormat.format приймає РЯДОК — перевірено тут, не за пам’яттю', () => {
    /*
     * ⛔ Це не тест продукту, а замір: рядковий аргумент `format()` — Intl v3
     * (ES2023), а `tsconfig.lib` клієнта — `ES2022`, тож у `number.ts` стоїть
     * звуження типу. Звуження типу про рантайм не доводить нічого — доводить
     * оце.
     */
    const formatted = (
      new Intl.NumberFormat('en', { maximumFractionDigits: 16 }) as unknown as {
        format(value: string): string;
      }
    ).format(Twenty);

    expect(formatted).toBe('1,234.1234567890123456');
  });

  it('стандартний Intl БЕЗ maximumFractionDigits обрізав би до трьох знаків', () => {
    // Саме тому `formatDecimal` рахує межу з самого значення.
    const truncated = (
      new Intl.NumberFormat('en') as unknown as { format(value: string): string }
    ).format(Twenty);

    expect(truncated).toBe('1,234.123');
  });
});

describe('formatDecimal — усі знаки доходять до екрана', () => {
  it('двадцять значущих цифр малюються повністю', () => {
    // ⛔ Мутаційна межа: будь-який `Number(...)` на шляху дав би
    // `1,234.1234567890124` (див. замір вище).
    expect(norm(formatDecimal(Twenty))).toBe('1,234.1234567890123456');

    setLanguage('kz');
    expect(norm(formatDecimal(Twenty))).toBe('1 234,1234567890123456');
  });

  it('роздільники — ті самі, що в formatNumber: en 1,234.5 / ru-kk 1 234,5', () => {
    expect(norm(formatDecimal('1234.5'))).toBe('1,234.5');

    setLanguage('ru');
    expect(norm(formatDecimal('1234.5'))).toBe('1 234,5');

    setLanguage('kz');
    expect(norm(formatDecimal('1234.5'))).toBe('1 234,5');
  });

  it('хвостові нулі на екран не потрапляють', () => {
    // ⛔ Рівно те, що `JSON.parse` робив мовчки до `e470777a`: `5.0000000000`
    // доїжджало як `5`. Показ зобов'язаний лишитися таким самим.
    expect(formatDecimal('5.0000000000')).toBe('5');
    expect(formatDecimal('12.3400000000')).toBe('12.34');
    expect(formatDecimal('0.0000000000')).toBe('0');
    expect(formatDecimal('-2.50')).toBe('-2.5');
  });

  it('число на вході теж приймається — Int і Lookup їдуть числами й далі', () => {
    expect(formatDecimal(5)).toBe('5');
    expect(norm(formatDecimal(1234.5))).toBe('1,234.5');
  });

  it('нечислове — null, щоб викликач показав значення як є', () => {
    // Порожній рядок тут означав би «комірка порожня», а вона не порожня:
    // сервер відповість `ECR-CELL-0422`, і до того оператор має бачити ввід.
    expect(formatDecimal('н/д')).toBeNull();
    expect(formatDecimal('')).toBeNull();
    expect(formatDecimal(null)).toBeNull();
    expect(formatDecimal(true)).toBeNull();
    expect(formatDecimal(Number.NaN)).toBeNull();

    // ⚠ Експонента відхиляється разом із `normalizeDecimal`: `decimal.ToString`
    // інваріантною культурою її не друкує, тож такий рядок прийшов не з
    // контракту. Другого правила тут не заводиться.
    expect(formatDecimal('1e3')).toBeNull();
  });
});
