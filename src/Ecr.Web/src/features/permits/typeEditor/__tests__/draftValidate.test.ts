import { describe, expect, it } from 'vitest';
import { addColumn, addTable, removeColumn, removeTable, updateColumn, newColumn, type TypeDraft } from '../draftModel';
import { validateDraft, validateForPublish, type DraftProblem } from '../draftValidate';
import { column, draftFixture, table } from './fixtures';

/**
 * Валідатори дзеркалять `PermitTypeDefinitionValidator` (PM-03): той самий `messageKey` на кожен
 * випадок, `ECR-PRM-0422`.
 *
 * Мутаційний доказ (виконано вручну, відновлено): у `columnProblem` `if (seen.has(…))` → `if (false)` валить
 * «columnDuplicate»; у `listProblem` `if (choices.length === 0)` → `if (false)` валить «typeOnlyNoChoices»; у
 * `validateDraft` `if (!table.columns.some(key))` → `if (false)` валить «tableWithoutKey».
 */

const options = { typeCode: 'WaterDischarge' };
const keys = (problems: DraftProblem[]): string[] => problems.map((p) => p.messageKey.replace('err.ECR-PRM-0422.', ''));
const check = (draft: TypeDraft): string[] => keys(validateDraft(draft, options));

function withColumn(draft: TypeDraft, tableCode: string, c: ReturnType<typeof column>): TypeDraft {
  // Повз `addColumn`, щоб покласти в копію й те, що модель відкинула б.
  return {
    ...draft,
    tables: draft.tables.map((t) => (t.code === tableCode ? { ...t, columns: [...t.columns, c] } : t)),
  };
}

describe('validateDraft: чиста чернетка', () => {
  it('фікстура проходить без відмов', () => {
    expect(check(draftFixture())).toEqual([]);
  });
});

describe('validateDraft: тип', () => {
  it('nameRequired — жодної мови з текстом', () => {
    expect(check({ ...draftFixture(), nameL10n: { en: '  ', ru: '' } })).toEqual(['nameRequired']);
  });
  it('categoryUnknown', () => {
    expect(check({ ...draftFixture(), category: 'Soil' })).toEqual(['categoryUnknown']);
  });
  it('registryCodeInvalid — код типу задовгий для реєстру шапки', () => {
    expect(keys(validateDraft(draftFixture(), { typeCode: 'T'.repeat(62) }))).toContain('registryCodeInvalid');
  });
});

describe('validateDraft: таблиці', () => {
  it('tableCodeInvalid і tableDuplicate (без огляду на регістр)', () => {
    const bad = { ...draftFixture(), tables: [table('1bad', [column('K', { key: true })])] };
    expect(check(bad)).toEqual(['tableCodeInvalid']);
    const dup = { ...draftFixture(), tables: [table('A', [column('K', { key: true })]), table('a', [column('K', { key: true })])] };
    expect(check(dup)).toContain('tableDuplicate');
  });

  it('registryCodeInvalid — реєстр таблиці задовгий', () => {
    const long = { ...draftFixture(), tables: [table('T'.repeat(50), [column('K', { key: true })])] };
    expect(check(long)).toContain('registryCodeInvalid');
  });

  it('nameRequired для таблиці', () => {
    const noName = { ...draftFixture(), tables: [table('A', [column('K', { key: true })], { nameL10n: { en: ' ' } })] };
    expect(check(noName)).toEqual(['nameRequired']);
  });

  it('tableWithoutKey', () => {
    const next = updateColumn(draftFixture(), 'Storage', 'Place', { key: false });
    const found = validateDraft(next, options);
    expect(keys(found)).toEqual(['tableWithoutKey']);
    expect(found[0]).toMatchObject({ tableCode: 'Storage', params: { tableCode: 'Storage' } });
  });
});

describe('validateDraft: стовпці', () => {
  const base = draftFixture();

  it('columnCodeInvalid, columnCodeReserved (PERMIT), columnDuplicate', () => {
    expect(check(withColumn(base, 'Storage', column('a-b')))).toEqual(['columnCodeInvalid']);
    expect(check(withColumn(base, 'Storage', column('permit')))).toEqual(['columnCodeReserved']);
    expect(check(withColumn(base, 'Storage', column('PLACE')))).toEqual(['columnDuplicate']);
  });

  it('nameRequired, columnTypeUnknown, groupCodeInvalid', () => {
    expect(check(withColumn(base, 'Storage', column('X', { nameL10n: { en: '' } })))).toEqual(['nameRequired']);
    expect(check(withColumn(base, 'Storage', column('X', { dataType: 'Blob' })))).toEqual(['columnTypeUnknown']);
    expect(check(withColumn(base, 'Storage', column('X', { group: '1 bad' })))).toEqual(['groupCodeInvalid']);
  });

  it('numericFormat: точність/масштаб лише для Decimal, 1..38, масштаб ≤ точності', () => {
    const decimal = (over: object) => check(withColumn(base, 'Storage', column('D', { dataType: 'Decimal', ...over })));
    expect(decimal({ precision: 12, scale: 3 })).toEqual([]);
    expect(decimal({ precision: 0 })).toEqual(['numericFormat']);
    expect(decimal({ precision: 39 })).toEqual(['numericFormat']);
    expect(decimal({ precision: 5, scale: 6 })).toEqual(['numericFormat']);
    expect(decimal({ precision: null, scale: 2 })).toEqual(['numericFormat']);
    expect(check(withColumn(base, 'Storage', column('I', { dataType: 'Int', precision: 5 })))).toEqual(['numericFormat']);
  });

  it('listForNonLookup', () => {
    expect(check(withColumn(base, 'Storage', column('X', { choices: ['a'] })))).toEqual(['listForNonLookup']);
  });
});

describe('validateDraft: списки', () => {
  const lookup = (over: object) => column('Pick', { dataType: 'Lookup', ...over });
  const base = draftFixture();
  const run = (c: ReturnType<typeof column>) => check(withColumn(base, 'Storage', c));

  it('listInconsistent — немає області чи зайві поля області', () => {
    expect(run(lookup({ listScope: null }))).toEqual(['listInconsistent']);
    expect(run(lookup({ listScope: 'Weird' }))).toEqual(['listInconsistent']);
    expect(run(lookup({ listScope: 'SharedRegistry', listRegistryCode: null }))).toEqual(['listInconsistent']);
    expect(run(lookup({ listScope: 'SharedRegistry', listRegistryCode: 'Substances', choices: ['a'] }))).toEqual(['listInconsistent']);
    expect(run(lookup({ listScope: 'TypeOnly', choices: ['a'], siblingTableCode: 'Discharge' }))).toEqual(['listInconsistent']);
    expect(run(lookup({ listScope: 'SamePermitTable', siblingTableCode: 'Discharge', listRegistryCode: 'R' }))).toEqual(['listInconsistent']);
  });

  it('спільний реєстр з кодом — гаразд', () => {
    expect(run(lookup({ listScope: 'SharedRegistry', listRegistryCode: 'Substances' }))).toEqual([]);
  });

  it('typeOnlyNoChoices і choicesInvalid (порожнє або повтор без огляду на регістр)', () => {
    expect(run(lookup({ listScope: 'TypeOnly', choices: [] }))).toEqual(['typeOnlyNoChoices']);
    expect(run(lookup({ listScope: 'TypeOnly', choices: null }))).toEqual(['typeOnlyNoChoices']);
    expect(run(lookup({ listScope: 'TypeOnly', choices: ['a', ' '] }))).toEqual(['choicesInvalid']);
    expect(run(lookup({ listScope: 'TypeOnly', choices: ['Yes', 'yes'] }))).toEqual(['choicesInvalid']);
    expect(run(lookup({ listScope: 'TypeOnly', choices: ['Yes', 'No'] }))).toEqual([]);
  });

  it('registryCodeInvalid — реєстр списку задовгий', () => {
    const long = lookup({ listScope: 'TypeOnly', choices: ['a'] });
    const draft = withColumn({ ...base, tables: [table('Storage', [column('K', { key: true })])] }, 'Storage', { ...long, code: 'C'.repeat(45) });
    expect(check(draft)).toContain('registryCodeInvalid');
  });

  it('siblingUnknown — своя або неіснуюча таблиця; гаразд — інша', () => {
    expect(run(lookup({ listScope: 'SamePermitTable', siblingTableCode: 'Storage' }))).toEqual(['siblingUnknown']);
    expect(run(lookup({ listScope: 'SamePermitTable', siblingTableCode: 'Nowhere' }))).toEqual(['siblingUnknown']);
    expect(run(lookup({ listScope: 'SamePermitTable', siblingTableCode: 'discharge' }))).toEqual([]);
  });

  it('siblingCycle — таблиці посилаються одна на одну; ланцюжок у параметрі', () => {
    const a = withColumn(base, 'Storage', lookup({ code: 'ToD', listScope: 'SamePermitTable', siblingTableCode: 'Discharge' }));
    const cyc = withColumn(a, 'Discharge', lookup({ code: 'ToS', listScope: 'SamePermitTable', siblingTableCode: 'Storage' }));
    const found = validateDraft(cyc, options);
    expect(keys(found)).toEqual(['siblingCycle']);
    expect(found[0]!.params['chain']).toMatch(/→/);
    expect(check(a)).toEqual([]);
  });

  it('видалення таблиці, на яку є посилання, дає siblingUnknown (клієнт не ховає це мовчки)', () => {
    const a = withColumn(base, 'Discharge', lookup({ code: 'ToS', listScope: 'SamePermitTable', siblingTableCode: 'Storage' }));
    expect(check(removeTable(a, 'Storage'))).toEqual(['siblingUnknown']);
  });
});

describe('validateDraft: перевірки типу', () => {
  const rule = (over: object) => ({ code: 'R1', expression: 'ROW.A >= 0', severity: 'Warn', nameL10n: { en: 'Not negative' }, ...over });
  const run = (rules: object[]) => check({ ...draftFixture(), rules: rules as TypeDraft['rules'] });

  it('ruleCodeInvalid, ruleDuplicate, ruleExpressionRequired, ruleSeverityUnknown, nameRequired', () => {
    expect(run([rule({})])).toEqual([]);
    expect(run([rule({ code: '1' })])).toEqual(['ruleCodeInvalid']);
    expect(run([rule({}), rule({ code: 'r1' })])).toEqual(['ruleDuplicate']);
    expect(run([rule({ expression: ' ' })])).toEqual(['ruleExpressionRequired']);
    expect(run([rule({ severity: 'Fatal' })])).toEqual(['ruleSeverityUnknown']);
    expect(run([rule({ nameL10n: { en: '' } })])).toEqual(['nameRequired']);
  });

  it('перевірки таблиці перевіряються так само', () => {
    const draft = draftFixture();
    const withRule = { ...draft, tables: draft.tables.map((t) => ({ ...t, rules: t.code === 'Storage' ? [rule({ expression: '' }) as never] : [] })) };
    expect(check(withRule)).toEqual(['ruleExpressionRequired']);
  });
});

describe('validateDraft: опублікований склад', () => {
  const published = draftFixture();

  it('tableRemoved, columnRemoved, columnRetyped, keyChanged', () => {
    const run = (draft: TypeDraft) => keys(validateDraft(draft, { ...options, published }));
    expect(run(published)).toEqual([]);
    expect(run(removeTable(published, 'Storage'))).toEqual(['tableRemoved']);
    expect(run(removeColumn(published, 'Discharge', 'Annual'))).toEqual(['columnRemoved']);
    expect(run(updateColumn(published, 'Discharge', 'Annual', { dataType: 'Int', precision: null, scale: null }))).toEqual(['columnRetyped']);
    expect(run(updateColumn(published, 'Discharge', 'Annual', { key: true }))).toEqual(['keyChanged']);
  });

  it('нова таблиця й новий стовпець опублікованому не заважають', () => {
    const next = addColumn(
      addTable(published, { code: 'Points', nameL10n: { en: 'P' }, columns: [column('N', { key: true })] }),
      'Storage',
      newColumn('Extra', { en: 'Extra' }),
    );
    expect(keys(validateDraft(next, { ...options, published }))).toEqual([]);
  });
});

describe('validateForPublish', () => {
  it('noTables', () => {
    expect(keys(validateForPublish({ ...draftFixture(), tables: [] }))).toEqual(['noTables']);
    expect(validateForPublish(draftFixture())).toEqual([]);
  });
});
