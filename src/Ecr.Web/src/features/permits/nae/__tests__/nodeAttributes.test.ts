import { describe, expect, it } from 'vitest';
import { EcrApiError } from '@/api/client';
import {
  EmptyNodeForm,
  buildNodeBody,
  formOfDetail,
  hasProblems,
  inheritedValues,
  serverFieldProblem,
  validateNode,
  type NodeDetail,
  type NodeFormState,
  type NodeFormTarget,
} from '../nodeAttributes';

/**
 * Модель форми атрибутів вузла структури NAE: перевірка, тіло запиту, успадкування регіону/району, відмови сервера.
 * Мутаційний доказ (вручну, відновлено): `after !== before` -> `true` у buildNodeBody валив «правка везе лише змінені».
 */

const fields = [
  { code: 'NaeCode', isRequired: true },
  { code: 'RegionCode', isRequired: true },
  { code: 'AreaCode', isRequired: true },
  { code: 'PointName', isRequired: false },
];

const level = { code: 'SRC', ordinal: 5, name: 'NAE', isOptional: false };
const createTarget: NodeFormTarget = { mode: 'create', parent: null, level };
const editTarget: NodeFormTarget = {
  mode: 'edit',
  node: { id: 42, parentId: 7, label: 'Pipe', ordinal: 1 },
};

const form = (over: Partial<NodeFormState> = {}): NodeFormState => ({ ...EmptyNodeForm, ...over });

function errorWithKey(messageKey: string): EcrApiError {
  return new EcrApiError({ status: 409, title: 'x', extensions2: { messageKey } } as never);
}

describe('validateNode', () => {
  it('порожня форма: назва і обов’язкові поля (район не рахується), код - якщо він не автоматичний', () => {
    const problems = validateNode(fields, form());

    expect(problems.code).toBe('required');
    expect(problems.display).toBe('required');
    expect(Object.keys(problems.values).sort()).toEqual(['NaeCode', 'RegionCode']);
    expect(hasProblems(problems)).toBe(true);
  });

  it('автоматичний код не вимагається', () => {
    const problems = validateNode(fields, form({ display: { en: 'Pipe' }, values: { NaeCode: '1085', RegionCode: 'ATYRAU' } }), true);

    expect(problems).toEqual({ values: {} });
    expect(hasProblems(problems)).toBe(false);
  });

  it('назва хоч однією мовою - досить; пробіли не рахуються', () => {
    expect(validateNode(fields, form({ display: { en: '  ', ru: 'Труба' } }), true).display).toBeUndefined();
    expect(validateNode(fields, form({ display: { en: '  ' } }), true).display).toBe('required');
  });
});

describe('buildNodeBody', () => {
  it('новий вузол: id null, рівень і батько з місця, всі непорожні значення, порожній район -> «-», код порожній за автокоду', () => {
    const body = buildNodeBody({
      registryId: 9,
      fields,
      target: createTarget,
      parentEntryId: 3,
      initial: form({ values: { RegionCode: 'ATYRAU' } }),
      current: form({ code: ' ', display: { en: 'Pipe' }, values: { NaeCode: '1085', RegionCode: 'ATYRAU', AreaCode: '' } }),
      codeAutomatic: true,
    });

    expect(body).toEqual({
      id: null,
      registryDefId: 9,
      code: '',
      display: { values: { en: 'Pipe' } },
      parentEntryId: 3,
      levelCode: 'SRC',
      values: { NaeCode: '1085', RegionCode: 'ATYRAU', AreaCode: '-' },
    });
  });

  it('правка: id вузла, рівень не чіпається, у значеннях лише змінені; стерте поле - null; стерта мова - порожній рядок', () => {
    const initial = form({ code: 'T-1', display: { en: 'Pipe', ru: 'Труба' }, values: { NaeCode: '1085', RegionCode: 'ATYRAU', AreaCode: '-', PointName: 'old' } });
    const body = buildNodeBody({
      registryId: 9,
      fields,
      target: editTarget,
      parentEntryId: 7,
      initial,
      current: { ...initial, display: { en: 'Pipe 2' }, values: { ...initial.values, NaeCode: '1086', PointName: '' } },
      codeAutomatic: true,
    });

    expect(body.id).toBe(42);
    expect(body.levelCode).toBeNull();
    expect(body.parentEntryId).toBe(7);
    expect(body.code).toBe('T-1');
    expect(body.display).toEqual({ values: { en: 'Pipe 2', ru: '' } });
    expect(body.values).toEqual({ NaeCode: '1086', PointName: null });
  });

  it('ручний код іде як є (обрізаний)', () => {
    const body = buildNodeBody({
      registryId: 1,
      fields: [],
      target: createTarget,
      parentEntryId: null,
      initial: EmptyNodeForm,
      current: form({ code: '  X1 ', display: { en: 'x' } }),
    });

    expect(body.code).toBe('X1');
  });
});

describe('inheritedValues', () => {
  const parent = { RegionCode: 'ATYRAU', AreaCode: 'ONSHORE' };

  it('регіон успадковується завжди, крім вузла-регіону; район - крім вузла-району; «-» не успадковується', () => {
    expect(inheritedValues(parent, 'SITE')).toEqual({ RegionCode: 'ATYRAU', AreaCode: 'ONSHORE' });
    expect(inheritedValues(parent, 'AREA')).toEqual({ RegionCode: 'ATYRAU' });
    expect(inheritedValues(parent, 'REGION')).toEqual({});
    expect(inheritedValues({ RegionCode: 'MANGISTAU', AreaCode: '-' }, 'SRC')).toEqual({ RegionCode: 'MANGISTAU' });
  });
});

describe('formOfDetail', () => {
  it('null із сервера - порожній рядок; назва - усі мови', () => {
    const detail = {
      code: 'T-1',
      displayL10n: { values: { en: 'Pipe' } },
      values: { NaeCode: '1085', PointName: null },
    } as unknown as NodeDetail;

    expect(formOfDetail(detail)).toEqual({ code: 'T-1', display: { en: 'Pipe' }, values: { NaeCode: '1085', PointName: '' } });
  });
});

describe('serverFieldProblem', () => {
  it.each([
    ['err.ECR-REG-0409.entryCodeTaken', 'codeTaken'],
    ['err.ECR-REG-0422.entryCodeAutomatic', 'codeAutomatic'],
    ['err.ECR-REG-4092.keyTaken', 'keyTaken'],
    ['err.ECR-REG-4092.keyWindowOverlap', 'keyTaken'],
  ])('%s -> %s', (key, expected) => {
    expect(serverFieldProblem(errorWithKey(key))).toBe(expected);
  });

  it('чужа відмова й не-API помилка поля не підсвічують', () => {
    expect(serverFieldProblem(errorWithKey('err.ECR-REG-0422.levelOrder'))).toBeNull();
    expect(serverFieldProblem(new Error('boom'))).toBeNull();
  });
});
