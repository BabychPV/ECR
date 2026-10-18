import { describe, expect, it } from 'vitest';
import {
  allRuleCodes,
  buildRule,
  countRules,
  flattenRules,
  validateRuleInput,
  withRule,
} from '../../checks/ruleOps';
import { withColumns, withTable } from '../applyOps';
import { column, permitType, table } from './fixtures';

/** Застосування результатів діалогів до визначення типу й перевірки (PM-08c). */

describe('withTable / withColumns', () => {
  it('таблиця додається в кінець з наступним порядком; вихідний тип не змінюється', () => {
    const type = permitType();
    const next = withTable(type, table({ code: 'Storage', order: 99 }));
    expect(next.tables.map((t) => [t.code, t.order])).toEqual([
      ['Limits', 1],
      ['Storage', 2],
    ]);
    expect(type.tables).toHaveLength(1);
  });

  it('стовпці додаються в кінець таблиці й перенумеровуються', () => {
    const type = permitType();
    const next = withColumns(type, 'Limits', [column({ code: 'Extra', order: 40 })]);
    expect(next.tables[0]?.columns.map((c) => [c.code, c.order])).toEqual([
      ['Name', 1],
      ['Annual', 2],
      ['Extra', 3],
    ]);
    expect(type.tables[0]?.columns).toHaveLength(2);
  });

  it('невідома таблиця — виняток, а не мовчазне ніщо', () => {
    expect(() => withColumns(permitType(), 'Nope', [column()])).toThrow(/Nope/);
  });
});

describe('перевірки', () => {
  const rule = (code: string, severity = 'Warn') => ({
    code,
    nameL10n: { en: code },
    expression: 'ROW.Annual >= 0',
    severity,
  });

  it('flattenRules: спершу рівня типу, далі по таблицях; область і ключ', () => {
    const type = permitType({
      rules: [rule('TypeRule')],
      tables: [table({ rules: [rule('TableRule', 'Block')] })],
    });
    const rows = flattenRules(type);
    expect(rows.map((r) => [r.key, r.scope.kind])).toEqual([
      ['permit:TypeRule', 'permit'],
      ['table:Limits:TableRule', 'table'],
    ]);
    expect(rows[1]?.scope).toEqual({ kind: 'table', tableCode: 'Limits', tableName: 'Limits' });
    expect(countRules(type)).toBe(2);
    expect(allRuleCodes(type)).toEqual(['TypeRule', 'TableRule']);
  });

  it('buildRule: код із назви, унікальний серед усіх перевірок типу', () => {
    const built = buildRule(
      { name: ' Months add up ', expression: ' SUM(ROW.M) = ROW.Y ', severity: 'Block' },
      ['Months_add_up'],
    );
    expect(built).toEqual({
      code: 'Months_add_up_2',
      nameL10n: { en: 'Months add up' },
      expression: 'SUM(ROW.M) = ROW.Y',
      severity: 'Block',
    });
  });

  it('validateRuleInput: назва й вираз обов’язкові', () => {
    expect(validateRuleInput({ name: ' ', expression: '' })).toEqual({ name: true, expression: true });
    expect(validateRuleInput({ name: 'A', expression: 'x' })).toEqual({ name: false, expression: false });
  });

  it('withRule: null — шапці типу, код таблиці — таблиці', () => {
    const type = permitType();
    const toType = withRule(type, rule('A'), null);
    expect(toType.rules.map((r) => r.code)).toEqual(['A']);
    expect(toType.tables[0]?.rules).toEqual([]);

    const toTable = withRule(type, rule('B'), 'Limits');
    expect(toTable.rules).toEqual([]);
    expect(toTable.tables[0]?.rules.map((r) => r.code)).toEqual(['B']);
  });

  it('withRule: невідома таблиця — виняток', () => {
    expect(() => withRule(permitType(), rule('A'), 'Nope')).toThrow(/Nope/);
  });
});
