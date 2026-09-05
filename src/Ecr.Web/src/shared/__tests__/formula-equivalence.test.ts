import { describe, it, expect } from 'vitest';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { evaluate, format, isError } from '@/shared/formula/evaluate';

/**
 * Клієнтський обчислювач — **лише підказка**; збережене значення завжди рахує
 * сервер (D-20). Але якщо підказка систематично розходиться з результатом,
 * користувач перестає їй вірити — тому спільний набір випадків має збігатися.
 *
 * Набір читається з того самого JSON, що й серверний тест еквівалентності
 * (`06b`).
 */
interface EquivalenceCase {
  function: string;
  expression: string;
  kind: 'number' | 'text' | 'boolean' | 'null' | 'error';
  expected: string;
}

const fixture = resolve(
  __dirname,
  '../../../../../tests/Ecr.TestKit/Fixtures/expression-equivalence.json',
);

const cases = (JSON.parse(readFileSync(fixture, 'utf-8')) as { cases: EquivalenceCase[] }).cases;

describe('Еквівалентність клієнт/сервер', () => {
  it('спільний набір виразів дає ті самі результати, що й сервер', () => {
    // ⚠ Читається САМЕ той файл, що й серверним тестом. Копія набору на
    // клієнті розійшлася б із серверною при першій же правці — і обидва
    // тести лишалися б зеленими.
    expect(cases.length).toBeGreaterThan(0);

    const divergent = cases
      .map((testCase) => ({ testCase, actual: format(evaluate(testCase.expression)) }))
      .filter(({ testCase, actual }) => actual !== testCase.expected)
      .map(
        ({ testCase, actual }) =>
          `${testCase.expression} → «${actual}», а сервер дає «${testCase.expected}»`,
      );

    expect(divergent).toEqual([]);
  });

  it('SUM порожньої множини дає 0, як на сервері', () => {
    expect(evaluate('SUM(NULL, NULL)')).toBe(0);
  });

  it('AVERAGE порожньої множини дає null, як на сервері', () => {
    // ⚠ Саме null, а не 0 і не помилка: «середнє ні з чого» не існує, а нуль
    // тут читався б як вимірювання, якого не робили.
    expect(evaluate('AVERAGE(NULL, NULL)')).toBeNull();
  });

  it('ROUND округлює від нуля, як на сервері', () => {
    // Math.round округлив би −2.5 до −2 — півкопійки на тисячі рядків дають
    // розбіжність, яку неможливо пояснити.
    expect(evaluate('ROUND(2.5, 0)')).toBe(3);
    expect(evaluate('ROUND(-2.5, 0)')).toBe(-3);
  });

  it('ділення на порожнечу — помилка, а не порожнеча', () => {
    const result = evaluate('1 / NULL');

    expect(isError(result)).toBe(true);
    expect(format(result)).toBe('#DIV/0');
  });
});
