import { normalizeDecimal } from '@/shared/format/decimal';
import {
  DefaultTolerance,
  serializeTestCase,
  type TestCaseForm,
  type TestCaseJson,
  type TestExpectedValue,
  type TestInputField,
} from './testCases';

/**
 * Помічники структурованої форми тесту (без UI): код, значення полів, перевірка
 * перед збереженням. Серіалізація в `inputJson`/`expectedJson` — `testCases.ts`.
 */

/** Код тесту: латиниця, цифри, підкреслення; з літери (як `EcrCode` на сервері). */
export const TestCodePattern = /^[A-Za-z][A-Za-z0-9_]*$/;

export type TestFormIssue =
  | 'codeMissing'
  | 'codeInvalid'
  | 'codeDuplicate'
  | 'periodMissing'
  | 'noExpected'
  | 'expectedNotNumber'
  | 'toleranceInvalid';

/** Рядок очікування у формі: значення — текст, поки людина друкує. */
export interface ExpectedRow {
  readonly output: string;
  readonly substanceEntryId: number | null;
  readonly text: string;
}

/** Наступний вільний код `TEST_<n>`. */
export function suggestTestCode(existing: readonly string[]): string {
  const taken = new Set(existing.map((code) => code.toUpperCase()));
  for (let n = existing.length + 1; ; n++) {
    const code = `TEST_${String(n)}`;
    if (!taken.has(code)) return code;
  }
}

/** Порожня форма для нового тесту. */
export function emptyTestForm(periodKey: number | null): TestCaseForm {
  return {
    period: periodKey ?? 0,
    documentId: null,
    tableInstanceId: null,
    rowKey: null,
    inputs: [],
    expected: [],
    tolerance: DefaultTolerance,
  };
}

/** Текст поля → значення: порожнє — «немає», число (кома або крапка) — число, інакше рядок. */
export function parseValueText(text: string): number | string | null {
  const trimmed = text.trim();
  if (trimmed === '') return null;
  if (/^[+-]?(?:\d+(?:[.,]\d+)?|[.,]\d+)(?:[eE][+-]?\d+)?$/.test(trimmed)) {
    const number = Number(trimmed.replace(',', '.'));
    if (Number.isFinite(number)) return number;
  }
  return trimmed;
}

/** Значення поля → текст у клітинці. */
export function valueText(value: number | string | null): string {
  return value === null ? '' : String(value);
}

/** Очікування форми → рядки з текстом значення. */
export function expectedRowsOf(expected: readonly TestExpectedValue[]): ExpectedRow[] {
  return expected.map((item) => ({
    output: item.output,
    substanceEntryId: item.substanceEntryId ?? null,
    text: String(item.value),
  }));
}

/** Текст очікування → число; `null` — не число. */
export function parseExpectedText(text: string): number | null {
  const value = parseValueText(text);
  return typeof value === 'number' ? value : null;
}

/** Рядки очікувань → значення форми (рядки без виходу чи з нечисловим текстом пропускаються). */
export function expectedFromRows(rows: readonly ExpectedRow[]): TestExpectedValue[] {
  const result: TestExpectedValue[] = [];
  for (const row of rows) {
    const output = row.output.trim();
    const value = parseExpectedText(row.text);
    if (output === '' || value === null) continue;

    result.push(
      row.substanceEntryId === null
        ? { output, value }
        : { output, substanceEntryId: row.substanceEntryId, value },
    );
  }
  return result;
}

/** Входи без значення не потрапляють у тест: порожнє поле — «не задано», а не нуль. */
export function filledInputs(inputs: readonly TestInputField[]): TestInputField[] {
  return inputs.filter((input) => input.name.trim() !== '' && input.value !== null);
}

/** Що не так із формою; порожній перелік — можна зберігати. */
export function validateTestForm(args: {
  readonly code: string;
  readonly isNew: boolean;
  readonly existingCodes: readonly string[];
  readonly period: number;
  readonly expectedRows: readonly ExpectedRow[];
  readonly tolerance: string;
}): TestFormIssue[] {
  const issues: TestFormIssue[] = [];
  const code = args.code.trim();

  if (code === '') issues.push('codeMissing');
  else if (!TestCodePattern.test(code)) issues.push('codeInvalid');
  else if (args.isNew && args.existingCodes.some((existing) => existing.toLowerCase() === code.toLowerCase())) {
    issues.push('codeDuplicate');
  }

  if (!(args.period > 0)) issues.push('periodMissing');

  const filled = args.expectedRows.filter((row) => row.output.trim() !== '');
  if (filled.length === 0) issues.push('noExpected');
  else if (filled.some((row) => parseExpectedText(row.text) === null)) issues.push('expectedNotNumber');

  if (normalizeDecimal(args.tolerance) === null) issues.push('toleranceInvalid');

  return issues;
}

/** Форма → поля тесту для `PUT …/tests/{code}`. */
export function testFormToJson(form: TestCaseForm, expectedRows: readonly ExpectedRow[]): TestCaseJson {
  const saved = serializeTestCase({
    ...form,
    inputs: filledInputs(form.inputs),
    expected: expectedFromRows(expectedRows),
  });

  return { ...saved, tolerance: normalizeDecimal(form.tolerance) ?? form.tolerance.trim() };
}
