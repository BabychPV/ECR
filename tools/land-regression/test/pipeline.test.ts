import assert from 'node:assert/strict';
import { test } from 'node:test';
import { EcrClient } from '../src/client.ts';
import { parseCsv, parseNumber } from '../src/csv.ts';
import { prepareRows, runRegression } from '../src/pipeline.ts';
import type { Mapping } from '../src/config.ts';

// ⚠ Макет: вигадані значення (Hours→tons_NOx 0.8 тощо), не дані прода.
const CSV = 'Id;Hours;tons_NOx;tons_301;gsec_NOx\nA;"1 000,5";0,8;0,64;1,333333\n';
const mapping: Mapping = {
  projectId: 1, sheetDefId: 7, tableCode: 'LAND', periodKey: 202601, rowKeyColumn: 'Id',
  inputs: { Hours: 'hours' },
  expected: { tons_NOx: 'tons_NOx', tons_301: 'tons_301', gsec_NOx: 'gsec_NOx' },
};

test('CSV: роздільник ; , десяткова кома, пробіл тисяч', () => {
  const rows = parseCsv(CSV);
  assert.equal(rows[0]!.Hours, '1 000,5');
  assert.equal(parseNumber(rows[0]!.Hours!), 1000.5);
  assert.equal(parseNumber(''), null);
});

test('prepareRows: відображення і помилка на відсутній колонці', () => {
  const [row] = prepareRows(parseCsv(CSV), mapping);
  assert.equal(row!.rowKey, 'A');
  assert.deepEqual(row!.inputs, [{ columnCode: 'hours', value: 1000.5 }]);
  assert.equal(row!.expected.length, 3);
  assert.throws(() => prepareRows(parseCsv('Id;Hours\nA;1\n'), mapping), /tons_NOx/);
});

function mockClient(resultValues: Record<string, number>, states: string[]) {
  const calls: string[] = [];
  const client = {
    createDocument: async () => (calls.push('doc'), { documentId: 5 }),
    tables: async () => [{ sheetDefId: 7, tableCode: 'LAND', tableInstanceId: 99 }],
    createRow: async (_d: number, _t: number, k: string) => (calls.push(`row:${k}`), { rowKey: k }),
    patchCells: async () => (calls.push('patch'), { appliedCells: 1 }),
    recalculate: async () => (calls.push('recalc'), { jobId: 'j#1' }),
    job: async () => ({ jobId: 'j#1', state: states.shift() ?? 'Succeeded' }),
    calculationResults: async () =>
      Object.entries(resultValues).map(([outputCode, value]) => ({ sourceRowKey: 'A', outputCode, value })),
  } as unknown as EcrClient;
  return { client, calls };
}

test('runRegression: повний цикл збігається, опитування чекає Succeeded', async () => {
  const { client, calls } = mockClient({ tons_NOx: 0.8, tons_301: 0.64, gsec_NOx: 1.3333331 }, ['Running', 'Running']);
  const res = await runRegression(client, mapping, prepareRows(parseCsv(CSV), mapping), { sleep: async () => {} });
  assert.deepEqual(calls, ['doc', 'row:A', 'patch', 'recalc']);
  assert.equal(res.report.mismatches.length, 0);
});

test('runRegression: розбіжність і провалена задача', async () => {
  const rows = prepareRows(parseCsv(CSV), mapping);
  const bad = mockClient({ tons_NOx: 0.9, tons_301: 0.64, gsec_NOx: 1.333333 }, []);
  const res = await runRegression(bad.client, mapping, rows, { sleep: async () => {} });
  assert.equal(res.report.mismatches[0]!.outputCode, 'tons_NOx');

  const failed = mockClient({}, ['Failed']);
  await assert.rejects(runRegression(failed.client, mapping, rows, { sleep: async () => {} }), /Failed/);
});
