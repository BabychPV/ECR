import { describe, it, expect } from 'vitest';
import { evaluate } from '@/shared/formula/evaluate';

/**
 * Аудит 2026-09-16, §10.8: рівність текст↔число на клієнті ЗВОДИЛА текст до
 * числа, а сервер цього не робить ніколи.
 *
 * ⛔ Поведінка сервера перевірена читанням, не здогадом
 * (`src/Ecr.Expressions/Evaluation/Evaluator.cs`, `AreEqual`):
 *
 *   1. обидва `null` → `true`; один `null` → `false`;
 *   2. ОБИДВА числа (`AsNumber()` віддає значення ЛИШЕ для
 *      `ExpressionValueType.Number` — для тексту це завжди `null`) →
 *      числове порівняння;
 *   3. **різні типи → `false`**, не помилка;
 *   4. однакові типи → `Equals`.
 *
 * ⚠ Тобто `"12" = 12` на сервері — `false`: текстовий літерал має тип
 * `Text`, його `AsNumber()` віддає `null`, типи різні. Клієнт же вів обидва
 * боки через `numeric()`, який розбирає `"12"` у `12`, — і відповідав `true`.
 * Підказка стверджувала рівність, якої збережений розрахунок не побачить.
 */
describe('Рівність у клієнтському обчислювачі повторює сервер (§10.8)', () => {
  it('число й текст, що виглядає числом, НЕ рівні — як `AreEqual` на сервері', () => {
    // ⛔ Мутаційний доказ: до фіксу `same()` зводило обидва боки через
    // `numeric()` і повертало саме `true`.
    expect(evaluate('"12" = 12')).toBe(false);
    expect(evaluate('"12" <> 12')).toBe(true);
  });

  it('булеве й число НЕ рівні: `AsNumber()` булевого на сервері — `null`', () => {
    expect(evaluate('TRUE = 1')).toBe(false);
    expect(evaluate('FALSE = 0')).toBe(false);
  });

  it('текст, що НЕ виглядає числом, і число — `false`, а не `#VALUE`', () => {
    // ⚠ Це вже працювало, і саме так поводиться сервер: різниця типів у
    // рівності — не помилка типу. Випадок лишається в наборі, щоб фікс вище
    // не «виправив» його на помилку.
    expect(evaluate('"abc" = 12')).toBe(false);
  });

  it('однотипні значення порівнюються як завжди', () => {
    expect(evaluate('"abc" = "abc"')).toBe(true);
    expect(evaluate('"abc" = "abd"')).toBe(false);
    expect(evaluate('12 = 12')).toBe(true);
    expect(evaluate('12 = 13')).toBe(false);
    expect(evaluate('TRUE = TRUE')).toBe(true);
    expect(evaluate('NULL = NULL')).toBe(true);
    expect(evaluate('NULL = 1')).toBe(false);
  });
});
