import type {
  ScriptOutlineDto,
  ScriptPreviewRowDto,
  ScriptPreviewValueDto,
} from './types';

/**
 * Структурована форма тесту методології ↔ наявні `inputJson` / `expectedJson`
 * (`calc.TestCase`). Чисті функції, без UI.
 */

export interface TestInputField {
  readonly name: string;
  readonly value: number | string | null;
  readonly unitId?: number | null;
}

export interface TestExpectedValue {
  readonly output: string;
  readonly substanceEntryId?: number;
  readonly value: number;
}

export interface TestCaseForm {
  readonly period: number;
  readonly documentId: number | null;
  readonly tableInstanceId: number | null;
  readonly rowKey: string | null;
  readonly inputs: readonly TestInputField[];
  readonly expected: readonly TestExpectedValue[];
  readonly tolerance: string;
}

export interface TestCaseJson {
  readonly inputJson: string;
  readonly expectedJson: string;
  readonly tolerance: string;
}

export const DefaultTolerance = '0.000001';

type JsonObject = Record<string, unknown>;

function asObject(value: unknown): JsonObject | null {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
    ? (value as JsonObject)
    : null;
}

function tryParse(text: string): unknown {
  try {
    return JSON.parse(text) as unknown;
  } catch {
    return undefined;
  }
}

function numberOrNull(value: unknown): number | null {
  return typeof value === 'number' && Number.isFinite(value) ? value : null;
}

/** Розбирає JSON тесту у форму; `null` — JSON не має очікуваної структури. */
export function parseTestCase(
  inputJson: string,
  expectedJson: string,
  tolerance: string,
): TestCaseForm | null {
  const input = asObject(tryParse(inputJson));
  const expected = asObject(tryParse(expectedJson));
  if (input === null || expected === null) return null;

  const periodKey = asObject(input['periodKey']);
  const inputs: TestInputField[] = [];
  const args = input['arguments'];
  if (Array.isArray(args)) {
    for (const raw of args) {
      const arg = asObject(raw);
      const name = arg?.['argumentCode'];
      if (arg === null || typeof name !== 'string') continue;

      const number = numberOrNull(arg['value']);
      const text = arg['valueString'];
      const unitId = numberOrNull(arg['unitId']);
      inputs.push({
        name,
        value: number ?? (typeof text === 'string' ? text : null),
        ...(unitId === null ? {} : { unitId }),
      });
    }
  }

  const values: TestExpectedValue[] = [];
  for (const [key, raw] of Object.entries(expected)) {
    const value = typeof raw === 'string' && raw.trim() !== '' ? Number(raw) : raw;
    if (typeof value !== 'number' || !Number.isFinite(value)) continue;

    const at = key.lastIndexOf('@');
    const id = at > 0 && /^\d+$/.test(key.slice(at + 1)) ? Number(key.slice(at + 1)) : null;
    values.push(
      id === null
        ? { output: key, value }
        : { output: key.slice(0, at), substanceEntryId: id, value },
    );
  }

  const sourceRowKey = input['sourceRowKey'];
  return {
    period: numberOrNull(periodKey?.['value']) ?? 0,
    documentId: numberOrNull(input['documentId']),
    tableInstanceId: numberOrNull(input['tableInstanceId']),
    rowKey: typeof sourceRowKey === 'string' ? sourceRowKey : null,
    inputs,
    expected: values,
    tolerance,
  };
}

/** Форма → поля тесту для збереження. */
export function serializeTestCase(form: TestCaseForm): TestCaseJson {
  const input: JsonObject = {};
  if (form.documentId !== null) input['documentId'] = form.documentId;
  if (form.tableInstanceId !== null) input['tableInstanceId'] = form.tableInstanceId;
  input['periodKey'] = { value: form.period };
  if (form.rowKey !== null) input['sourceRowKey'] = form.rowKey;
  input['arguments'] = form.inputs.map((field) => {
    const arg: JsonObject = { argumentCode: field.name };
    if (typeof field.value === 'number') arg['value'] = field.value;
    else if (typeof field.value === 'string') arg['valueString'] = field.value;
    if (field.unitId !== undefined && field.unitId !== null) arg['unitId'] = field.unitId;
    return arg;
  });

  const expected: JsonObject = {};
  for (const item of form.expected) {
    const key =
      item.substanceEntryId === undefined ? item.output : `${item.output}@${String(item.substanceEntryId)}`;
    expected[key] = item.value;
  }

  return {
    inputJson: JSON.stringify(input),
    expectedJson: JSON.stringify(expected),
    tolerance: form.tolerance,
  };
}

export interface PreviewTestContext {
  readonly outline: ScriptOutlineDto;
  readonly documentId: number | null;
  readonly periodKey: number;
  readonly tableInstanceId: number | null;
  /** Код речовини → Id запису довідника речовин (для `OUT@<id>`). */
  readonly substanceEntryIds?: Readonly<Record<string, number>>;
  readonly tolerance?: string;
}

/** Значення рядка перегляду → значення тестового входу. */
export function previewValueToInput(value: ScriptPreviewValueDto): number | string | null {
  if (value.error !== undefined && value.error !== null && value.error !== '') return null;
  const text = value.text.trim();
  if (text === '' || value.kind.toLowerCase() === 'null') return null;
  if (/^[+-]?(?:\d+(?:[.,]\d+)?|[.,]\d+)(?:[eE][+-]?\d+)?$/.test(text)) {
    const number = Number(text.replace(',', '.'));
    if (Number.isFinite(number)) return number;
  }
  return text;
}

/**
 * Тест із рядка перегляду: входи — з контуру (`outline.inputs`), значення — зі
 * списку значень рядка за іменем (де є); очікування — числові значення виходів.
 */
export function testFromPreviewRow(
  row: ScriptPreviewRowDto,
  context: PreviewTestContext,
): TestCaseForm {
  const byName = new Map<string, ScriptPreviewValueDto>();
  for (const value of row.values) {
    if (value.substance === undefined || value.substance === null) byName.set(value.name, value);
  }

  const inputs: TestInputField[] = context.outline.inputs.map((input) => {
    const found = byName.get(input.name);
    return { name: input.name, value: found === undefined ? null : previewValueToInput(found) };
  });

  const expected: TestExpectedValue[] = [];
  for (const output of context.outline.outputs) {
    for (const value of row.values) {
      if (value.name !== output.code) continue;
      if (value.error !== undefined && value.error !== null && value.error !== '') continue;
      const number = previewValueToInput(value);
      if (typeof number !== 'number') continue;

      if (value.substance === undefined || value.substance === null || value.substance === '') {
        expected.push({ output: output.code, value: number });
      } else {
        const id = context.substanceEntryIds?.[value.substance];
        if (id !== undefined) {
          expected.push({ output: output.code, substanceEntryId: id, value: number });
        }
      }
    }
  }

  return {
    period: context.periodKey,
    documentId: context.documentId,
    tableInstanceId: context.tableInstanceId,
    rowKey: row.rowKey,
    inputs,
    expected,
    tolerance: context.tolerance ?? DefaultTolerance,
  };
}
