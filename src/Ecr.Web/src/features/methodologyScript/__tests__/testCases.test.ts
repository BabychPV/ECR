import { describe, expect, it } from 'vitest';
import {
  DefaultTolerance,
  parseTestCase,
  previewValueToInput,
  serializeTestCase,
  testFromPreviewRow,
  type TestCaseForm,
} from '../testCases';
import type { ScriptPreviewRowDto } from '../types';
import { outline as outlineOf, previewRow, previewValue } from './fixtures';

const inputJson = JSON.stringify({
  documentId: 12,
  tableInstanceId: 34,
  periodKey: { value: 202604 },
  sourceRowKey: 'r1',
  arguments: [
    { argumentCode: 'Hours', value: 12.5 },
    { argumentCode: 'Fuel', valueString: 'Gasoline' },
    { argumentCode: 'Status', value: 7, unitId: 3 },
    { argumentCode: 'Empty' },
  ],
});
const expectedJson = JSON.stringify({ OUT: 1.5, 'OUT_S@42': 0.25 });

describe('тест методології: JSON ↔ форма', () => {
  it('розбір', () => {
    const form = parseTestCase(inputJson, expectedJson, '0.01');
    expect(form).toEqual<TestCaseForm>({
      period: 202604,
      documentId: 12,
      tableInstanceId: 34,
      rowKey: 'r1',
      inputs: [
        { name: 'Hours', value: 12.5 },
        { name: 'Fuel', value: 'Gasoline' },
        { name: 'Status', value: 7, unitId: 3 },
        { name: 'Empty', value: null },
      ],
      expected: [
        { output: 'OUT', value: 1.5 },
        { output: 'OUT_S', substanceEntryId: 42, value: 0.25 },
      ],
      tolerance: '0.01',
    });
  });

  it('round-trip: форма → JSON → форма', () => {
    const form = parseTestCase(inputJson, expectedJson, '0.01');
    expect(form).not.toBeNull();
    const json = serializeTestCase(form as TestCaseForm);
    expect(json.tolerance).toBe('0.01');
    expect(parseTestCase(json.inputJson, json.expectedJson, json.tolerance)).toEqual(form);
  });

  it('serialize: формат полів збігається з наявним', () => {
    const json = serializeTestCase({
      period: 202601,
      documentId: null,
      tableInstanceId: null,
      rowKey: null,
      inputs: [
        { name: 'A', value: 1 },
        { name: 'B', value: 'x' },
        { name: 'C', value: null },
      ],
      expected: [{ output: 'OUT', value: 2 }, { output: 'S', substanceEntryId: 5, value: 3 }],
      tolerance: '0',
    });
    expect(JSON.parse(json.inputJson)).toEqual({
      periodKey: { value: 202601 },
      arguments: [{ argumentCode: 'A', value: 1 }, { argumentCode: 'B', valueString: 'x' }, { argumentCode: 'C' }],
    });
    expect(JSON.parse(json.expectedJson)).toEqual({ OUT: 2, 'S@5': 3 });
  });

  it('некоректний JSON — null', () => {
    expect(parseTestCase('{ not', '{}', '0')).toBeNull();
    expect(parseTestCase('[]', '{}', '0')).toBeNull();
    expect(parseTestCase('{}', 'null', '0')).toBeNull();
  });

  it('порожній об’єкт дає порожню форму; нечислове очікування пропускається', () => {
    const form = parseTestCase('{}', '{"A":"abc","B":"2.5"}', '0');
    expect(form).toMatchObject({ period: 0, inputs: [], expected: [{ output: 'B', value: 2.5 }] });
  });

  it('ключ із «@» без числа лишається іменем виходу', () => {
    const form = parseTestCase('{}', '{"A@x":1}', '0');
    expect(form?.expected).toEqual([{ output: 'A@x', value: 1 }]);
  });
});

const outline = outlineOf({
  inputs: [
    { name: 'Hours', optional: false },
    { name: 'Fuel', optional: false },
    { name: 'Unknown', optional: true },
  ],
  outputs: [
    { code: 'OUT', unit: 't', perSubstance: false },
    { code: 'OUT_S', unit: 't', perSubstance: true },
  ],
  substances: ['301', '304'],
});

describe('тест із рядка перегляду', () => {
  const row: ScriptPreviewRowDto = previewRow({
    rowKey: 'row-9',
    rowNumber: 9,
    values: [
      previewValue({ name: 'Hours', kind: 'number', text: '12,5' }),
      previewValue({ name: 'Fuel', kind: 'text', text: 'Gasoline' }),
      previewValue({ name: 'OUT', kind: 'number', text: '3.5' }),
      previewValue({ name: 'OUT_S', substance: '301', kind: 'number', text: '1' }),
      previewValue({ name: 'OUT_S', substance: '304', kind: 'number', text: '2' }),
      previewValue({ name: 'OUT_S', substance: '999', kind: 'number', text: '9' }),
    ],
  });

  it('входи з контуру зі значеннями рядка; відсутні — null', () => {
    const form = testFromPreviewRow(row, {
      outline,
      documentId: 1,
      periodKey: 202604,
      tableInstanceId: 2,
      substanceEntryIds: { '301': 11, '304': 14 },
    });
    expect(form.inputs).toEqual([
      { name: 'Hours', value: 12.5 },
      { name: 'Fuel', value: 'Gasoline' },
      { name: 'Unknown', value: null },
    ]);
    expect(form.rowKey).toBe('row-9');
    expect(form.period).toBe(202604);
    expect(form.tolerance).toBe(DefaultTolerance);
  });

  it('входи рядка з сервера (inputs) мають перевагу: ім’я, число, текст, одиниця', () => {
    const withInputs: ScriptPreviewRowDto = {
      ...row,
      inputs: [
        { name: 'Hours', value: '12.5', text: null, unitId: 7 },
        { name: 'Fuel', value: null, text: 'Gasoline', unitId: null },
        { name: 'Jan', value: '0', text: null, unitId: null },
        { name: 'Empty', value: null, text: null, unitId: null },
      ],
    };
    const form = testFromPreviewRow(withInputs, { outline, documentId: 1, periodKey: 202604, tableInstanceId: 2 });
    expect(form.inputs).toEqual([
      { name: 'Hours', value: 12.5, unitId: 7 },
      { name: 'Fuel', value: 'Gasoline' },
      { name: 'Jan', value: 0 },
      { name: 'Empty', value: null },
    ]);
  });

  it('порожній inputs — запасний шлях через контур і values', () => {
    const empty: ScriptPreviewRowDto = { ...row, inputs: [] };
    const form = testFromPreviewRow(empty, { outline, documentId: 1, periodKey: 202604, tableInstanceId: 2 });
    expect(form.inputs.map((i) => i.name)).toEqual(['Hours', 'Fuel', 'Unknown']);
  });

  it('очікування: вихід і вихід по речовині (лише з відомим Id)', () => {
    const form = testFromPreviewRow(row, {
      outline,
      documentId: null,
      periodKey: 1,
      tableInstanceId: null,
      substanceEntryIds: { '301': 11, '304': 14 },
      tolerance: '0.1',
    });
    expect(form.expected).toEqual([
      { output: 'OUT', value: 3.5 },
      { output: 'OUT_S', substanceEntryId: 11, value: 1 },
      { output: 'OUT_S', substanceEntryId: 14, value: 2 },
    ]);
    expect(form.tolerance).toBe('0.1');
  });

  it('значення з помилкою не стає очікуванням', () => {
    const bad: ScriptPreviewRowDto = {
      ...row,
      values: [previewValue({ name: 'OUT', kind: 'error', text: '#DIV/0', error: '#DIV/0' })],
    };
    const form = testFromPreviewRow(bad, { outline, documentId: null, periodKey: 1, tableInstanceId: null });
    expect(form.expected).toEqual([]);
  });

  it('previewValueToInput: число, текст, порожнє, помилка', () => {
    expect(previewValueToInput(previewValue({ name: 'a', kind: 'number', text: '1,5' }))).toBe(1.5);
    expect(previewValueToInput(previewValue({ name: 'a', kind: 'text', text: 'abc' }))).toBe('abc');
    expect(previewValueToInput(previewValue({ name: 'a', kind: 'null', text: '' }))).toBeNull();
    expect(previewValueToInput(previewValue({ name: 'a', kind: 'error', text: '#N/A', error: '#N/A' }))).toBeNull();
  });
});
