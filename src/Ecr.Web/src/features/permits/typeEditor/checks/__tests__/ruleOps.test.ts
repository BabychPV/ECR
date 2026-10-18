import { describe, expect, it } from 'vitest';
import { allRuleCodes, buildRule, countRules, flattenRules, validateRuleInput } from '../ruleOps';
import { permitType, table } from '../../dialogs/__tests__/fixtures';

/** Перевірки типу: плоский перелік, код із назви, форма (PM-08c). Додавання в тип — `draftModel.addRule`. */

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
});
