import { describe, expect, it } from 'vitest';
import {
  applyGate,
  buildOptions,
  confirmedOwnerKeys,
  EmptyDraft,
  fileStepProblem,
  normalizeArea,
  patternsToText,
  previewSignature,
  profileCodeProblem,
  resolutionKey,
  sortIssues,
  specProblem,
  textToPatterns,
  toRequestResolutions,
  unresolved,
  withField,
  withNewField,
  withoutField,
  type ImportDraft,
} from '../import/importModel';
import { preview, xlsx } from './airFixtures';

const base: ImportDraft = { ...EmptyDraft, file: xlsx(), region: ' atyrau ', area: '', profileCode: 'ERA_TABLE_33' };
const missing = preview().preview.missing;

describe('buildOptions', () => {
  it('регіон у верхньому регістрі, порожня ділянка - "-", профіль без специфікації', () => {
    expect(buildOptions(base)).toEqual({ profileCode: 'ERA_TABLE_33', region: 'ATYRAU', area: '-' });
  });

  it('незбережена специфікація перемагає профіль', () => {
    const spec = { fields: [{ name: 'A' }] };
    const options = buildOptions({ ...base, spec });
    expect(options.spec).toBe(spec);
    expect(options.profileCode).toBeUndefined();
  });

  it('рішення, власники й відбиток їдуть лише коли задані', () => {
    const draft: ImportDraft = {
      ...base,
      resolutions: {
        [resolutionKey(missing[0]!)]: { choice: 'create', createUnder: '3' },
        [resolutionKey(missing[1]!)]: { choice: 'skip', createUnder: '' },
      },
    };
    const options = buildOptions(draft, { missing, ownerKeys: ['7', '9'], fingerprint: 'fp-1' });
    expect(options.resolutions).toEqual([
      { kind: 'nae', code: '3544', action: 'create', createUnder: '3' },
      { kind: 'nae', code: '3545', action: 'skip' },
    ]);
    expect(options.confirmedOtherOwners).toEqual(['7', '9']);
    expect(options.expectedFingerprint).toBe('fp-1');
    expect(buildOptions(base).confirmedOtherOwners).toBeUndefined();
  });
});

describe('рішення про відсутні НАЕ', () => {
  it('"створити" без вузла не потрапляє в запит і лишається нерозв\'язаним', () => {
    const resolutions = { [resolutionKey(missing[0]!)]: { choice: 'create' as const, createUnder: '' } };
    expect(toRequestResolutions(missing, resolutions)).toEqual([]);
    expect(unresolved(missing, resolutions)).toHaveLength(2);
  });

  it('усі розв\'язані - нерозв\'язаних немає', () => {
    const resolutions = {
      [resolutionKey(missing[0]!)]: { choice: 'skip' as const, createUnder: '' },
      [resolutionKey(missing[1]!)]: { choice: 'create' as const, createUnder: '3' },
    };
    expect(unresolved(missing, resolutions)).toEqual([]);
  });
});

describe('applyGate: коли можна застосувати', () => {
  const resolved = {
    [resolutionKey(missing[0]!)]: { choice: 'skip' as const, createUnder: '' },
    [resolutionKey(missing[1]!)]: { choice: 'skip' as const, createUnder: '' },
  };

  it('чисто: помилок немає, усе розв\'язано, заміна чужих підтверджена, перегляд свіжий', () => {
    expect(applyGate(preview(), { ...base, resolutions: resolved, confirmedReplace: true }, false)).toEqual({ allowed: true, reasons: [] });
  });

  it('кожна причина блокує окремо', () => {
    expect(applyGate(preview({ canApply: false }), { ...base, resolutions: resolved, confirmedReplace: true }, false).reasons).toEqual(['errors']);
    expect(applyGate(preview(), { ...base, confirmedReplace: true }, false).reasons).toEqual(['unresolved']);
    expect(applyGate(preview(), { ...base, resolutions: resolved }, false).reasons).toEqual(['confirm']);
    expect(applyGate(preview(), { ...base, resolutions: resolved, confirmedReplace: true }, true).reasons).toEqual(['stale']);
  });

  it('підтвердження не потрібне, коли чужих дозволів немає', () => {
    const own = preview({
      missing: [],
      replace: { confirmationRequired: false, otherOwners: [], thisOwner: preview().preview.replace.thisOwner },
    });
    expect(applyGate(own, base, false).allowed).toBe(true);
  });
});

describe('confirmedOwnerKeys', () => {
  it('без підтвердження людини ключів немає, з підтвердженням - усі чужі власники', () => {
    expect(confirmedOwnerKeys(preview(), false)).toEqual([]);
    expect(confirmedOwnerKeys(preview(), true)).toEqual(['7', '9']);
  });
});

describe('previewSignature', () => {
  it('змінюється від рішення, регіону, ділянки, специфікації й файлу; не від прапорця підтвердження', () => {
    const sig = previewSignature(base, missing);
    expect(previewSignature({ ...base, confirmedReplace: true }, missing)).toBe(sig);
    expect(previewSignature({ ...base, region: 'MANGISTAU' }, missing)).not.toBe(sig);
    expect(previewSignature({ ...base, area: 'ONSHORE' }, missing)).not.toBe(sig);
    expect(previewSignature({ ...base, spec: { fields: [] } }, missing)).not.toBe(sig);
    expect(previewSignature({ ...base, file: xlsx('other.xlsx') }, missing)).not.toBe(sig);
    expect(
      previewSignature({ ...base, resolutions: { [resolutionKey(missing[0]!)]: { choice: 'skip', createUnder: '' } } }, missing),
    ).not.toBe(sig);
  });
});

describe('крок 1', () => {
  it('файл, розширення, регіон', () => {
    expect(fileStepProblem(EmptyDraft)).toBe('file');
    expect(fileStepProblem({ ...base, file: xlsx('a.xls') })).toBe('extension');
    expect(fileStepProblem({ ...base, region: '  ' })).toBe('region');
    expect(fileStepProblem({ ...base, area: '' })).toBe('area');
    expect(fileStepProblem({ ...base, area: '-' })).toBeNull(); // «-» - регіон без ділянок, вибір зі списку scope-codes
    expect(fileStepProblem({ ...base, area: 'OFFSHORE' })).toBeNull();
  });

  it('нормалізація ділянки', () => {
    expect(normalizeArea('')).toBe('-');
    expect(normalizeArea(' offshore ')).toBe('OFFSHORE');
  });
});

describe('редактор специфікації', () => {
  it('шаблони заголовка: розділювач |', () => {
    expect(textToPatterns(' a | b |  | c ')).toEqual(['a', 'b', 'c']);
    expect(patternsToText(['a', 'b'])).toBe('a | b');
  });

  it('операції над полями не чіпають решту специфікації', () => {
    const spec = { sheet: 'S', fields: [{ name: 'A' }, { name: 'B' }] };
    expect(withField(spec, 1, { required: true }).fields).toEqual([{ name: 'A' }, { name: 'B', required: true }]);
    expect(withoutField(spec, 0).fields).toEqual([{ name: 'B' }]);
    expect(withNewField(spec, 'C').fields?.at(-1)?.name).toBe('C');
    expect(withNewField(spec, 'C').sheet).toBe('S');
  });

  it('ім\'я обов\'язкове й унікальне (без урахування регістру)', () => {
    expect(specProblem({ fields: [{ name: ' ' }] })).toBe('nameRequired');
    expect(specProblem({ fields: [{ name: 'A' }, { name: 'a' }] })).toBe('nameDuplicate');
    expect(specProblem({ fields: [{ name: 'A' }, { name: 'B' }] })).toBeNull();
  });

  it('код профілю', () => {
    expect(profileCodeProblem('MY_PROFILE_1')).toBe(false);
    expect(profileCodeProblem('my')).toBe(true);
    expect(profileCodeProblem('1ABC')).toBe(true);
  });
});

describe('sortIssues', () => {
  it('помилки першими, далі за рядком', () => {
    const sorted = sortIssues([
      { severity: 'Warning', messageKey: 'k', row: 1 },
      { severity: 'Error', messageKey: 'k', row: 9 },
      { severity: 'Error', messageKey: 'k', row: 2 },
    ]);
    expect(sorted.map((issue) => `${issue.severity}${String(issue.row)}`)).toEqual(['Error2', 'Error9', 'Warning1']);
  });
});
