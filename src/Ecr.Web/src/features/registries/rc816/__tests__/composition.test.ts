import { describe, expect, it } from 'vitest';
import type { RegistryDefinitionDto } from '@/api/types';
import {
  childSumRules,
  compositionFromSave,
  compositionIssues,
  compositionOf,
  compositionSave,
  formatDecimal,
  parseDecimal,
  sumDecimals,
  withinTolerance,
} from '../composition';
import { buildSaveRequest, emptyField } from '../../definition';

/**
 * Композиція довідників (`ФВ-8.16`): читання зв'язку з опису, тіло збереження нового поля, Σ
 * індикатора «Сума дочірніх».
 */

type Relation = RegistryDefinitionDto['relations'][number];
type Rule = RegistryDefinitionDto['rules'][number];

function definition(over: Partial<RegistryDefinitionDto> = {}): RegistryDefinitionDto {
  return {
    id: 7,
    code: 'GAS_COMPOSITION',
    nameL10n: { values: { en: 'Gas composition' } },
    isTemporal: false,
    sourceKind: 'Local',
    definitionVersion: 1,
    dataRevision: 1,
    fields: [],
    relations: [],
    rules: [],
    mappings: [],
    ...over,
  };
}

const CaseComposition: Relation = {
  kind: 'Composition',
  fieldCode: 'CASE',
  targetRegistryDefId: 6,
  targetRegistryCode: 'STREAM_CASE',
  linkKind: null,
  linkCount: null,
  onParentDelete: 'Cascade',
};

const ComponentReference: Relation = {
  kind: 'Cascade',
  fieldCode: 'COMPONENT',
  targetRegistryDefId: 3,
  targetRegistryCode: 'COMPONENT',
  linkKind: null,
  linkCount: null,
};

function rule(parametersJson: string | null, over: Partial<Rule> = {}): Rule {
  return {
    id: 1,
    code: 'SUM_100',
    expression: 'ABS(…) <= 0.5',
    isActive: true,
    messageL10n: { values: { en: 'Σ' } },
    parametersJson,
    ruleKind: 'Expression',
    severity: 'Warning',
    ...over,
  };
}

describe('compositionOf', () => {
  it('знаходить поле композиції серед зв\'язків і не бере посилання', () => {
    expect(compositionOf(definition({ relations: [ComponentReference, CaseComposition] }))).toEqual({
      fieldCode: 'CASE',
      parentRegistryDefId: 6,
      parentRegistryCode: 'STREAM_CASE',
      onParentDelete: 'Cascade',
    });
    expect(compositionOf(definition({ relations: [ComponentReference] }))).toBeNull();
  });

  it('без політики — Restrict, як типово на сервері', () => {
    const { onParentDelete: _, ...noPolicy } = CaseComposition;
    expect(compositionOf(definition({ relations: [noPolicy] }))?.onParentDelete).toBe('Restrict');
  });
});

describe('збереження нового поля композиції', () => {
  it('композиція йде з relationKind, політикою і ОБОВ\'ЯЗКОВІСТЮ — інакше сервер відмовить compositionNotRequired', () => {
    const field = { ...emptyField('Lookup'), code: 'CASE', name: 'Case', lookupRegistryDefId: 6, relationKind: 'Composition' as const, onParentDelete: 'Cascade' as const };
    const body = buildSaveRequest(definition(), [], [field], 'склад кейсу', 'en');
    const saved = body.fields.at(-1);

    expect(saved).toMatchObject({ code: 'CASE', relationKind: 'Composition', onParentDelete: 'Cascade', isRequired: true });
  });

  it('звичайне посилання лишається необов\'язковим і без композиції', () => {
    const field = { ...emptyField('Lookup'), code: 'COMPONENT', name: 'Component', lookupRegistryDefId: 3 };
    const saved = buildSaveRequest(definition(), [], [field], 'посилання', 'en').fields.at(-1);

    expect(saved).toMatchObject({ relationKind: null, onParentDelete: null, isRequired: false });
  });

  it('прапорець композиції на полі не-Lookup ігнорується', () => {
    expect(compositionSave({ dataType: 'String', relationKind: 'Composition' })).toEqual({ relationKind: null, onParentDelete: null });
  });

  it('чернетка сервера повертає ознаки композиції у форму', () => {
    expect(compositionFromSave({ relationKind: 'Composition', onParentDelete: null })).toEqual({
      relationKind: 'Composition',
      onParentDelete: 'Restrict',
    });
    expect(compositionFromSave({ relationKind: 'Reference', onParentDelete: null })).toEqual({});
  });
});

describe('compositionIssues', () => {
  const draft = { dataType: 'Lookup', lookupRegistryDefId: 6, relationKind: 'Composition' as const };

  it('друге поле композиції — порушення, і наявне, і серед нових', () => {
    expect(compositionIssues(definition({ relations: [CaseComposition] }), draft, [draft])).toEqual([
      'registries.rc816.issueMoreThanOne',
    ]);
    const second = { ...draft, lookupRegistryDefId: 8 };
    expect(compositionIssues(definition(), draft, [draft, second])).toEqual(['registries.rc816.issueMoreThanOne']);
  });

  it('ціль — сам довідник і темпоральний довідник', () => {
    expect(compositionIssues(definition({ isTemporal: true }), { ...draft, lookupRegistryDefId: 7 }, [])).toEqual([
      'registries.rc816.issueTargetSelf',
      'registries.rc816.issueTemporal',
    ]);
  });

  it('звичайне посилання не має порушень композиції', () => {
    expect(compositionIssues(definition({ relations: [CaseComposition] }), { ...draft, relationKind: null }, [])).toEqual([]);
  });
});

describe('childSumRules', () => {
  it('читає параметри шаблону childSum, числом і рядком', () => {
    const parsed = childSumRules(
      definition({
        rules: [rule('{"template":"childSum","child":"GAS_COMPOSITION","field":"MOL_PCT","target":100,"tolerance":"0.5"}')],
      }),
    );
    expect(parsed).toEqual([
      { code: 'SUM_100', child: 'GAS_COMPOSITION', field: 'MOL_PCT', target: '100', tolerance: '0.5', severity: 'Warning' },
    ]);
  });

  it('вимкнене, чужого шаблону, без цілі чи з битим JSON — пропускається', () => {
    const base = '{"template":"childSum","child":"G","field":"F","target":100}';
    expect(
      childSumRules(
        definition({
          rules: [
            rule(base, { isActive: false }),
            rule('{"template":"other","child":"G","field":"F","target":100}'),
            rule('{"template":"childSum","child":"G","field":"F"}'),
            rule('{not json'),
            rule(null),
          ],
        }),
      ),
    ).toEqual([]);
    expect(childSumRules(definition({ rules: [rule(base)] }))[0]?.tolerance).toBe('0');
  });
});

describe('Σ без втрати знаків', () => {
  it('0.1 + 0.2 = 0.3 рівно, а не 0.30000000000000004', () => {
    expect(formatDecimal(sumDecimals(['0.1', '0.2']).sum)).toBe('0.3');
  });

  it('склад із шістьма знаками дає 100.0002, як на сервері', () => {
    const { sum } = sumDecimals(['1.435977', '12.424669', '86.139554', '0.000100', '-0.000100']);
    expect(formatDecimal(sum)).toBe('100.0002');
  });

  it('експонента, кома людини, пробіли-розряди; нечислове й порожнє — окремо', () => {
    expect(formatDecimal(parseDecimal('1E-05') ?? { units: 0n, scale: 0 })).toBe('0.00001');
    expect(formatDecimal(parseDecimal('12,5') ?? { units: 0n, scale: 0 })).toBe('12.5');
    expect(formatDecimal(parseDecimal('1 234.5') ?? { units: 0n, scale: 0 })).toBe('1234.5');
    expect(parseDecimal('abc')).toBeNull();
    expect(parseDecimal('-')).toBeNull();
    // ⚠ Неоднозначне (розряди чи дріб?) у Σ не вгадується — сервер його однаково відхилить (L9-04).
    expect(parseDecimal('1,234')).toBeNull();
    expect(formatDecimal(parseDecimal('1 234,5') ?? { units: 0n, scale: 0 })).toBe('1234.5');
    expect(sumDecimals(['1', 'x', '', null, undefined])).toEqual({ sum: { units: 1n, scale: 0 }, skipped: 1 });
  });

  it('експонента за межею decimal — не число, а не зависання чи RangeError у рендері (L9-10)', () => {
    const started = performance.now();
    expect(parseDecimal('1e-1000000000')).toBeNull();
    expect(parseDecimal('1e100000000')).toBeNull();
    expect(performance.now() - started).toBeLessThan(50);
    expect(sumDecimals(['1', '1e-100000000'])).toEqual({ sum: { units: 1n, scale: 0 }, skipped: 1 });
    expect(formatDecimal(parseDecimal('1e-28') ?? { units: 0n, scale: 0 })).toBe(`0.${'0'.repeat(27)}1`);
  });

  it('межа допуску включна (<=), як у згенерованому виразі', () => {
    const sum = sumDecimals(['99.5']).sum;
    expect(withinTolerance(sum, '100', '0.5')).toBe(true);
    expect(withinTolerance(sumDecimals(['99.49']).sum, '100', '0.5')).toBe(false);
    expect(withinTolerance(sumDecimals(['100.5']).sum, '100', '0.5')).toBe(true);
    expect(withinTolerance(sumDecimals(['100.51']).sum, '100', '0.5')).toBe(false);
    expect(withinTolerance(sum, 'x', '0.5')).toBeNull();
  });
});
