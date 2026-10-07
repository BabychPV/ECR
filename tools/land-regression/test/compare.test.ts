import assert from 'node:assert/strict';
import { test } from 'node:test';
import { compare, formatReport, withinTolerance } from '../src/compare.ts';

// ⚠ Усі числа — ВИГАДАНІ (макет), не дані замовника.
const expected = [
  { rowKey: 'r1', outputCode: 'tons_NOx', value: 0.8 },
  { rowKey: 'r1', outputCode: 'tons_301', value: 0.64 },
  { rowKey: 'r1', outputCode: 'gsec_NOx', value: 1.333333 },
];

test('допуск: відносний 1e-6', () => {
  assert.ok(withinTolerance(0.8, 0.8000004, 1e-6));
  assert.ok(!withinTolerance(0.8, 0.8000009, 1e-6));
  assert.ok(withinTolerance(0, 0, 1e-6));
  assert.ok(!withinTolerance(0, 0.1, 1e-6));
});

test('усе збігається', () => {
  const r = compare(expected, expected.map((e) => ({ sourceRowKey: e.rowKey, outputCode: e.outputCode, value: e.value })));
  assert.equal(r.matched, 3);
  assert.deepEqual(r.mismatches, []);
});

test('розбіжність значення', () => {
  const actual = [
    { sourceRowKey: 'r1', outputCode: 'tons_NOx', value: 0.8 },
    { sourceRowKey: 'r1', outputCode: 'tons_301', value: 0.65 },
    { sourceRowKey: 'r1', outputCode: 'gsec_NOx', value: 1.333333 },
  ];
  const r = compare(expected, actual);
  assert.equal(r.matched, 2);
  assert.equal(r.mismatches.length, 1);
  assert.equal(r.mismatches[0]!.kind, 'differs');
  assert.equal(r.mismatches[0]!.outputCode, 'tons_301');
  assert.ok(formatReport(r).includes('tons_301'));
});

test('відсутнє поле у результатах', () => {
  const r = compare(expected, [{ sourceRowKey: 'r1', outputCode: 'tons_NOx', value: 0.8 }]);
  assert.deepEqual(r.mismatches.map((m) => [m.kind, m.outputCode]), [['missing', 'tons_301'], ['missing', 'gsec_NOx']]);
});

test('чужий рядок не рахується збігом; зайві викиди лише за прапорцем', () => {
  const actual = [{ sourceRowKey: 'r2', outputCode: 'tons_NOx', value: 0.8 }];
  assert.equal(compare(expected, actual).mismatches.length, 3);
  const withExtra = compare(expected, actual, 1e-6, { reportUnexpected: true });
  assert.equal(withExtra.mismatches.filter((m) => m.kind === 'unexpected').length, 1);
});
