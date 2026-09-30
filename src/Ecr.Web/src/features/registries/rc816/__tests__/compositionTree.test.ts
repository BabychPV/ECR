import { describe, expect, it, vi } from 'vitest';
import type { RegistryDefDto, RegistryDefinitionDto } from '@/api/types';
import { buildCompositionTree } from '../compositionTree';

/**
 * Ланцюжок композиції «знаходиться сам» (`ФВ-8.16`, §8.4): STREAM → STREAM_CASE → GAS_COMPOSITION,
 * а COMPONENT (просте посилання складу) до дерева не входить.
 */

type Field = RegistryDefDto['fields'][number];

function lookup(code: string, target: number): Field {
  return {
    id: target * 100,
    code,
    nameL10n: { values: { en: code } },
    dataType: 'Lookup',
    isRequired: true,
    isScopeField: false,
    lookupRegistryDefId: target,
    unitId: null,
  };
}

function listed(id: number, code: string, fields: Field[] = []): RegistryDefDto {
  return { id, code, nameL10n: { values: { en: code } }, fields, isHierarchical: false, isTemporal: false, sourceKind: 'Local' };
}

function described(
  id: number,
  code: string,
  relations: RegistryDefinitionDto['relations'] = [],
  rules: RegistryDefinitionDto['rules'] = [],
): RegistryDefinitionDto {
  return {
    id,
    code,
    nameL10n: { values: { en: code } },
    isTemporal: false,
    sourceKind: 'Local',
    definitionVersion: 1,
    dataRevision: 1,
    fields: [],
    relations,
    rules,
    mappings: [],
  };
}

function composedInto(field: string, target: number, targetCode: string): RegistryDefinitionDto['relations'][number] {
  return { kind: 'Composition', fieldCode: field, targetRegistryDefId: target, targetRegistryCode: targetCode, linkKind: null, linkCount: null, onParentDelete: 'Cascade' };
}

const SumRule: RegistryDefinitionDto['rules'][number] = {
  id: 1,
  code: 'SUM_100',
  expression: '…',
  isActive: true,
  messageL10n: { values: {} },
  parametersJson: '{"template":"childSum","child":"GAS_COMPOSITION","field":"MOL_PCT","target":100,"tolerance":0.5}',
  ruleKind: 'Expression',
  severity: 'Warning',
};

const Registries: RegistryDefDto[] = [
  listed(1, 'STREAM'),
  listed(2, 'STREAM_CASE', [lookup('STREAM', 1)]),
  listed(3, 'GAS_COMPOSITION', [lookup('CASE', 2), lookup('COMPONENT', 4)]),
  listed(4, 'COMPONENT'),
  // Посилання на STREAM, але не композиція — до дерева не входить.
  listed(5, 'FLARE_LOG', [lookup('STREAM', 1)]),
];

const Definitions: Record<string, RegistryDefinitionDto> = {
  STREAM: described(1, 'STREAM'),
  STREAM_CASE: described(2, 'STREAM_CASE', [composedInto('STREAM', 1, 'STREAM')], [SumRule]),
  GAS_COMPOSITION: described(3, 'GAS_COMPOSITION', [
    composedInto('CASE', 2, 'STREAM_CASE'),
    { kind: 'Cascade', fieldCode: 'COMPONENT', targetRegistryDefId: 4, targetRegistryCode: 'COMPONENT', linkKind: null, linkCount: null },
  ]),
  COMPONENT: described(4, 'COMPONENT'),
  FLARE_LOG: described(5, 'FLARE_LOG', [
    { kind: 'Cascade', fieldCode: 'STREAM', targetRegistryDefId: 1, targetRegistryCode: 'STREAM', linkKind: null, linkCount: null },
  ]),
};

describe('buildCompositionTree', () => {
  it('будує ланцюжок частин і відкидає прості посилання', async () => {
    const load = vi.fn(async (code: string) => Definitions[code] as RegistryDefinitionDto);
    const tree = await buildCompositionTree(Definitions.STREAM as RegistryDefinitionDto, Registries, load);

    expect(tree.children.map((node) => node.definition.code)).toEqual(['STREAM_CASE']);
    const cases = tree.children[0];
    expect(cases?.link).toMatchObject({ fieldCode: 'STREAM', parentRegistryDefId: 1 });
    expect(cases?.children.map((node) => node.definition.code)).toEqual(['GAS_COMPOSITION']);

    // COMPONENT не має поля на склад — його опис навіть не читається.
    expect(load).not.toHaveBeenCalledWith('COMPONENT');
  });

  it('правило «Сума дочірніх» батька дістається саме тій дитині, яку воно рахує', async () => {
    const tree = await buildCompositionTree(
      Definitions.STREAM as RegistryDefinitionDto,
      Registries,
      async (code) => Definitions[code] as RegistryDefinitionDto,
    );

    const composition = tree.children[0]?.children[0];
    expect(composition?.sums).toEqual([
      { code: 'SUM_100', child: 'GAS_COMPOSITION', field: 'MOL_PCT', target: '100', tolerance: '0.5', severity: 'Warning' },
    ]);
    expect(tree.children[0]?.sums).toEqual([]);
  });

  it('коло в даних (повз опис) не зациклює обхід', async () => {
    const cyclic: Record<string, RegistryDefinitionDto> = {
      A: described(10, 'A', [composedInto('B', 11, 'B')]),
      B: described(11, 'B', [composedInto('A', 10, 'A')]),
    };
    const load = vi.fn(async (code: string) => cyclic[code] as RegistryDefinitionDto);
    const tree = await buildCompositionTree(
      cyclic.A as RegistryDefinitionDto,
      [listed(10, 'A', [lookup('B', 11)]), listed(11, 'B', [lookup('A', 10)])],
      load,
    );

    expect(tree.children.map((node) => node.definition.code)).toEqual(['B']);
    expect(tree.children[0]?.children).toEqual([]);
    expect(load).toHaveBeenCalledTimes(1);
  });
});
