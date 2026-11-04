import { describe, expect, it } from 'vitest';
import { EcrApiError } from '@/api/client';
import {
  EmptyNodeForm,
  buildNodeBody,
  fieldsForLevel,
  formOfDetail,
  hasProblems,
  isServerField,
  serverFieldProblem,
  serverNaeCodeHolder,
  targetLevelCode,
  validateNode,
  type NodeDetail,
  type NodeFormState,
  type NodeFormTarget,
} from '../nodeAttributes';

/**
 * Модель форми вузла структури NAE за типом вузла: поля свого рівня, поля сервера, перевірка (назва англійською, номер NAE),
 * тіло запиту, відмови сервера.
 * Мутаційний доказ (вручну, відновлено): `after !== before` -> `true` у buildNodeBody валив «правка везе лише змінені»;
 * прибраний фільтр рівня у `fieldsForLevel` валив «поля за рівнем».
 */

const field = (code: string, over: Record<string, unknown> = {}) => ({ id: 1, code, isRequired: false, ...over }) as never;

const allFields = [
  field('NaeCode', { isRequired: true, levelCode: 'SRC' }),
  field('RegionCode', { levelCode: 'SRC', isReadOnly: true }),
  field('AreaCode', { levelCode: 'SRC', isReadOnly: true }),
  field('AfPath', { levelCode: null, isReadOnly: true }),
  field('SourceType', { levelCode: 'SRC' }),
  field('Comment'),
];

const level = { code: 'SRC', ordinal: 5, name: 'NAE', isOptional: false };
const createTarget: NodeFormTarget = { mode: 'create', parent: null, level };
const editTarget: NodeFormTarget = {
  mode: 'edit',
  node: { id: 42, parentId: 7, label: 'Pipe', ordinal: 1, levelCode: 'SRC' },
};

const form = (over: Partial<NodeFormState> = {}): NodeFormState => ({ ...EmptyNodeForm, ...over });

function errorWithKey(messageKey: string, extra: Record<string, unknown> = {}): EcrApiError {
  return new EcrApiError({ status: 409, title: 'x', extensions2: { messageKey, ...extra } } as never);
}

describe('fieldsForLevel / isServerField / targetLevelCode', () => {
  it('поля свого рівня й поля всіх рівнів; чужий рівень не показується', () => {
    expect(fieldsForLevel(allFields, 'SRC').map((f: { code: string }) => f.code)).toEqual(['NaeCode', 'RegionCode', 'AreaCode', 'SourceType', 'Comment']);
    expect(fieldsForLevel(allFields, 'AREA').map((f: { code: string }) => f.code)).toEqual(['Comment']);
    expect(fieldsForLevel(allFields, null).map((f: { code: string }) => f.code)).toEqual(['Comment']);
  });

  it('поле сервера - лише isReadOnly=true; відсутня ознака - поле вводу', () => {
    expect(isServerField({ isReadOnly: true })).toBe(true);
    expect(isServerField({ isReadOnly: false })).toBe(false);
    expect(isServerField({})).toBe(false);
  });

  it('RegionCode пишеться лише на REGION, AreaCode - лише на AREA; на інших рівнях - поле сервера', () => {
    const region = { code: 'RegionCode', isReadOnly: false };
    const area = { code: 'AreaCode', isReadOnly: false };

    expect(isServerField(region, 'REGION')).toBe(false);
    expect(isServerField(region, 'AREA')).toBe(true);
    expect(isServerField(region, 'SRC')).toBe(true);
    expect(isServerField(region)).toBe(true);
    expect(isServerField(area, 'AREA')).toBe(false);
    expect(isServerField(area, 'REGION')).toBe(true);
    expect(isServerField(area, 'SITE')).toBe(true);
    // Інші поля йдуть за ознакою поля, а не за рівнем.
    expect(isServerField({ code: 'AfPath', isReadOnly: true }, 'REGION')).toBe(true);
    expect(isServerField({ code: 'Comment', isReadOnly: false }, 'REGION')).toBe(false);
  });

  it('рівень форми: нового вузла - вибраний, наявного - власний', () => {
    expect(targetLevelCode(createTarget)).toBe('SRC');
    expect(targetLevelCode(editTarget)).toBe('SRC');
    expect(targetLevelCode({ mode: 'edit', node: { id: 1, parentId: null, label: 'x', ordinal: 1 } })).toBeNull();
  });
});

describe('validateNode', () => {
  const inputs = [field('NaeCode', { isRequired: true }), field('SourceType')];

  it('порожня форма: назва англійською, номер NAE, код - якщо він не автоматичний', () => {
    const problems = validateNode(inputs, form());

    expect(problems.code).toBe('required');
    expect(problems.display).toBe('required');
    expect(Object.keys(problems.values)).toEqual(['NaeCode']);
    expect(hasProblems(problems)).toBe(true);
  });

  it('автоматичний код не вимагається', () => {
    const problems = validateNode(inputs, form({ display: { en: 'Pipe' }, values: { NaeCode: '1085' } }), true);

    expect(problems).toEqual({ values: {} });
    expect(hasProblems(problems)).toBe(false);
  });

  it('назва англійською обов’язкова: російська чи казахська самі не рятують; пробіли не рахуються', () => {
    expect(validateNode(inputs, form({ display: { en: '  ', ru: 'Труба', kz: 'Құбыр' }, values: { NaeCode: '1' } }), true).display).toBe('required');
    expect(validateNode(inputs, form({ display: { en: 'Pipe' }, values: { NaeCode: '1' } }), true).display).toBeUndefined();
  });
});

describe('buildNodeBody', () => {
  const inputs = [field('NaeCode'), field('SourceType'), field('Comment')];

  it('новий вузол: id null, рівень і батько з місця, всі непорожні значення, код порожній за автокоду', () => {
    const body = buildNodeBody({
      registryId: 9,
      fields: inputs,
      target: createTarget,
      parentEntryId: 3,
      initial: EmptyNodeForm,
      current: form({ code: ' ', display: { en: 'Pipe', ru: 'Труба', kz: 'Құбыр' }, values: { NaeCode: '36_pod', SourceType: '' } }),
      codeAutomatic: true,
    });

    expect(body).toEqual({
      id: null,
      registryDefId: 9,
      code: '',
      display: { values: { en: 'Pipe', ru: 'Труба', kz: 'Құбыр' } },
      parentEntryId: 3,
      levelCode: 'SRC',
      values: { NaeCode: '36_pod' },
    });
  });

  it('правка: id вузла, рівень не чіпається, у значеннях лише змінені; стерте поле - null; стерта мова - порожній рядок', () => {
    const initial = form({ code: 'T-1', display: { en: 'Pipe', ru: 'Труба' }, values: { NaeCode: '0036_pod', SourceType: 'old' } });
    const body = buildNodeBody({
      registryId: 9,
      fields: inputs,
      target: editTarget,
      parentEntryId: 7,
      initial,
      current: { ...initial, display: { en: 'Pipe 2' }, values: { ...initial.values, NaeCode: '37_pod', SourceType: '' } },
      codeAutomatic: true,
    });

    expect(body.id).toBe(42);
    expect(body.levelCode).toBeNull();
    expect(body.parentEntryId).toBe(7);
    expect(body.code).toBe('T-1');
    expect(body.display).toEqual({ values: { en: 'Pipe 2', ru: '' } });
    expect(body.values).toEqual({ NaeCode: '37_pod', SourceType: null });
  });

  it('поля сервера в тіло не потрапляють, бо форма не передає їх у fields', () => {
    const inputFields = fieldsForLevel(allFields, 'SRC').filter((f) => !isServerField(f));
    const body = buildNodeBody({
      registryId: 1,
      fields: inputFields,
      target: createTarget,
      parentEntryId: null,
      initial: EmptyNodeForm,
      current: form({ display: { en: 'x' }, values: { NaeCode: '1', RegionCode: 'HACK', AreaCode: 'HACK', AfPath: 'HACK' } }),
      codeAutomatic: true,
    });

    expect(Object.keys(body.values)).toEqual(['NaeCode']);
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

describe('formOfDetail', () => {
  it('null із сервера - порожній рядок; назва - усі мови', () => {
    const detail = {
      code: 'T-1',
      displayL10n: { values: { en: 'Pipe' } },
      values: { NaeCode: '1085', SourceType: null },
    } as unknown as NodeDetail;

    expect(formOfDetail(detail)).toEqual({ code: 'T-1', display: { en: 'Pipe' }, values: { NaeCode: '1085', SourceType: '' } });
  });
});

describe('serverFieldProblem', () => {
  it.each([
    ['err.ECR-REG-0409.entryCodeTaken', 'codeTaken'],
    ['err.ECR-REG-0422.entryCodeAutomatic', 'codeAutomatic'],
    ['err.ECR-REG-4092.keyTaken', 'keyTaken'],
    ['err.ECR-REG-4092.keyWindowOverlap', 'keyTaken'],
    ['err.ECR-REG-4094.naeCodeTaken', 'naeCodeTaken'],
    ['err.ECR-REG-0422.naeNameEnRequired', 'nameEnRequired'],
  ])('%s -> %s', (key, expected) => {
    expect(serverFieldProblem(errorWithKey(key))).toBe(expected);
  });

  it('чужа відмова й не-API помилка поля не підсвічують', () => {
    expect(serverFieldProblem(errorWithKey('err.ECR-REG-0422.levelOrder'))).toBeNull();
    expect(serverFieldProblem(errorWithKey('err.ECR-REG-0422.naeMoveChangesScope'))).toBeNull();
    // Зміна рівня з SRC і перенос у SRC без номера - сповіщення з каталогу; форма й «Move to…» рівень не міняють (levelCode: null).
    expect(serverFieldProblem(errorWithKey('err.ECR-REG-0422.naeSourceLevelChange'))).toBeNull();
    expect(serverFieldProblem(errorWithKey('err.ECR-REG-0422.requiredFieldsMissing'))).toBeNull();
    expect(serverFieldProblem(new Error('boom'))).toBeNull();
  });
});

describe('serverNaeCodeHolder', () => {
  it('тримач номера: нормалізований номер, область, запис; без messageKey 4094 - null', () => {
    const error = errorWithKey('err.ECR-REG-4094.naeCodeTaken', { naeCode: '0036_pod', regionCode: 'ATYRAU', areaCode: 'OFFSHORE', entryId: '501', entryCode: 'T-501' });

    expect(serverNaeCodeHolder(error)).toEqual({ naeCode: '0036_pod', region: 'ATYRAU', area: 'OFFSHORE', entryCode: 'T-501', entryId: 501 });
    expect(serverNaeCodeHolder(errorWithKey('err.ECR-REG-4092.keyTaken', { entryCode: 'T-1' }))).toBeNull();
    expect(serverNaeCodeHolder(errorWithKey('err.ECR-REG-4094.naeCodeTaken'))?.entryId).toBeNull();
  });
});
