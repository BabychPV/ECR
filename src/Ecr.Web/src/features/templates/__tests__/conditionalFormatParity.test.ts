import { describe, expect, it } from 'vitest';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import {
  emptyRule,
  ruleMatches,
  ruleMatchesValue,
  sampleValue,
  type ConditionalRule,
  type ConditionOperator,
  type RuleCellValue,
} from '../conditionalFormat';

/**
 * Паритет умовного форматування клієнт ↔ сервер (`ФВ-2.7`).
 *
 * ⚠ Читається САМЕ той файл, що й серверним `ConditionalFormatParityTests.cs`:
 * копія набору на клієнті розійшлася б із серверною при першій же правці, і
 * обидва тести лишалися б зеленими. Саме так прожила розбіжність «`ne 100` на
 * тексті «abc»»: панель фарбувала, а зріз сервера й Excel — ні.
 *
 * ⛔ Мутаційний доказ (2026-10-01, окремий detached-worktree, без пушу):
 * повернути текстове порівняння для `eq`/`ne` — червоніють «ne: текст abc» і
 * «eq: текст …»; `Number` замість точного порівняння — червоніє «точність
 * decimal»; прибрати впорядкування меж у `between` — червоніє «межі навпаки»;
 * прибрати виняток U+FEFF у `isBlank` — червоніє «empty: BOM».
 */
interface ParityCase {
  readonly id: string;
  readonly operator: ConditionOperator;
  readonly value: string | null;
  readonly valueTo: string | null;
  readonly cell: readonly [string, string | null] | null;
  readonly matches: boolean;
}

const fixture = resolve(__dirname, '../../../../../../tests/Ecr.TestKit/Fixtures/conditional-format-parity.json');
const cases = (JSON.parse(readFileSync(fixture, 'utf-8')) as { cases: ParityCase[] }).cases;

function ruleOf(testCase: ParityCase): ConditionalRule {
  return {
    ...emptyRule('A'),
    operator: testCase.operator,
    value: testCase.value ?? '',
    valueTo: testCase.valueTo ?? '',
    backgroundHex: '#ff0000',
  };
}

/** Те саме розгортання, що `CellValueMapping.ToRuleValue`: число — лише `Numeric`. */
function cellOf(testCase: ParityCase): RuleCellValue {
  const cell = testCase.cell;
  if (cell === null || cell[0] === 'Empty') return null;

  return cell[0] === 'Numeric' ? { kind: 'number', value: cell[1] ?? '' } : { kind: 'other', value: cell[1] ?? '' };
}

describe('Паритет умовного форматування з сервером', () => {
  it('набір покриває всі оператори', () => {
    expect(new Set(cases.map((testCase) => testCase.operator))).toEqual(
      new Set(['gt', 'ge', 'lt', 'le', 'eq', 'ne', 'between', 'empty', 'notEmpty']),
    );
  });

  it('ruleMatchesValue дає те саме, що сервер, на кожному векторі', () => {
    const divergent = cases
      .filter((testCase) => ruleMatchesValue(ruleOf(testCase), cellOf(testCase)) !== testCase.matches)
      .map((testCase) => `${testCase.id}: сервер дає ${String(testCase.matches)}`);

    expect(divergent).toEqual([]);
  });

  it('приклад у панелі: де рядок однозначно задає тип, результат той самий', () => {
    // ⚠ Приклад — лише рядок без типу колонки: текст «5», ідентифікатор
    // довідника чи одиниці він бачить числом. Такі вектори тут пропускаються
    // свідомо — сітка й Excel беруть результат із сервера, не з прикладу.
    const representable = cases.filter(
      (testCase) => sampleValue(testCase.cell?.[1] ?? null)?.kind === cellOf(testCase)?.kind,
    );
    const skipped = cases.filter((testCase) => !representable.includes(testCase));

    expect(skipped.every((testCase) => testCase.cell?.[0] !== 'Numeric')).toBe(true);
    expect(skipped.length).toBeLessThanOrEqual(12);

    const divergent = representable
      .filter((testCase) => ruleMatches(ruleOf(testCase), testCase.cell?.[1] ?? null) !== testCase.matches)
      .map((testCase) => `${testCase.id}: сервер дає ${String(testCase.matches)}`);

    expect(divergent).toEqual([]);
  });
});
