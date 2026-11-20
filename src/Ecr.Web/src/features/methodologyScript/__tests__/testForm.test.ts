import { describe, expect, it } from 'vitest';
import { parseTestCase } from '../testCases';
import {
  emptyTestForm,
  expectedFromRows,
  expectedRowsOf,
  filledInputs,
  parseExpectedText,
  parseValueText,
  suggestTestCode,
  testFormToJson,
  validateTestForm,
  valueText,
} from '../testForm';

describe('форма тесту', () => {
  it('код пропонується наступним вільним', () => {
    expect(suggestTestCode([])).toBe('TEST_1');
    expect(suggestTestCode(['TEST_1', 'test_2'])).toBe('TEST_3');
    expect(suggestTestCode(['A', 'TEST_2'])).toBe('TEST_3');
  });

  it('текст поля → число, рядок або «немає»', () => {
    expect(parseValueText('')).toBeNull();
    expect(parseValueText(' 12,5 ')).toBe(12.5);
    expect(parseValueText('-3')).toBe(-3);
    expect(parseValueText('Gas')).toBe('Gas');
    expect(valueText(null)).toBe('');
    expect(valueText(4)).toBe('4');
    expect(parseExpectedText('abc')).toBeNull();
    expect(parseExpectedText('1e3')).toBe(1000);
  });

  it('входи без значення в тест не потрапляють', () => {
    expect(
      filledInputs([
        { name: 'A', value: 1 },
        { name: 'B', value: null },
        { name: ' ', value: 5 },
        { name: 'C', value: 'x' },
      ]),
    ).toEqual([
      { name: 'A', value: 1 },
      { name: 'C', value: 'x' },
    ]);
  });

  it('рядки очікувань ↔ значення форми, речовина йде як OUT@id', () => {
    const rows = expectedRowsOf([
      { output: 'OUT', value: 2.5 },
      { output: 'OUT_S', substanceEntryId: 14, value: 1 },
    ]);
    expect(rows).toEqual([
      { output: 'OUT', substanceEntryId: null, text: '2.5' },
      { output: 'OUT_S', substanceEntryId: 14, text: '1' },
    ]);
    expect(
      expectedFromRows([
        ...rows,
        { output: '', substanceEntryId: null, text: '5' },
        { output: 'X', substanceEntryId: null, text: 'abc' },
      ]),
    ).toEqual([
      { output: 'OUT', value: 2.5 },
      { output: 'OUT_S', substanceEntryId: 14, value: 1 },
    ]);
  });

  const valid = {
    code: 'TEST_1',
    isNew: true,
    existingCodes: [] as string[],
    period: 202604,
    expectedRows: [{ output: 'OUT', substanceEntryId: null, text: '8' }],
    tolerance: '0.0001',
  };

  it('перевірка: валідна форма не має зауважень', () => {
    expect(validateTestForm(valid)).toEqual([]);
  });

  it('перевірка: код, дубль, період, порожні очікування, нечисло, допуск', () => {
    expect(validateTestForm({ ...valid, code: ' ' })).toContain('codeMissing');
    expect(validateTestForm({ ...valid, code: '1A' })).toContain('codeInvalid');
    expect(validateTestForm({ ...valid, code: 'test_1', existingCodes: ['TEST_1'] })).toContain('codeDuplicate');
    // Для наявного тесту код не змінюється — дубль не рахується.
    expect(validateTestForm({ ...valid, isNew: false, existingCodes: ['TEST_1'] })).toEqual([]);
    expect(validateTestForm({ ...valid, period: 0 })).toContain('periodMissing');
    expect(validateTestForm({ ...valid, expectedRows: [] })).toContain('noExpected');
    expect(
      validateTestForm({ ...valid, expectedRows: [{ output: 'OUT', substanceEntryId: null, text: 'x' }] }),
    ).toContain('expectedNotNumber');
    expect(validateTestForm({ ...valid, tolerance: 'abc' })).toContain('toleranceInvalid');
  });

  it('серіалізація: без порожніх входів, допуск нормалізований, читається назад тим самим розбором', () => {
    const form = {
      ...emptyTestForm(202604),
      documentId: 7,
      tableInstanceId: 9,
      rowKey: 'row-1',
      inputs: [
        { name: 'A', value: 4 },
        { name: 'B', value: null },
      ],
      tolerance: '0.00010',
    };
    const json = testFormToJson(form, [{ output: 'OUT', substanceEntryId: null, text: '8' }]);

    const back = parseTestCase(json.inputJson, json.expectedJson, json.tolerance);
    expect(back).toMatchObject({
      period: 202604,
      documentId: 7,
      tableInstanceId: 9,
      rowKey: 'row-1',
      inputs: [{ name: 'A', value: 4 }],
      expected: [{ output: 'OUT', value: 8 }],
    });
    expect(json.tolerance).toBe('0.0001');
  });
});
