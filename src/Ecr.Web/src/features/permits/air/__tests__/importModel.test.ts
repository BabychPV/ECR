import { describe, expect, it } from 'vitest';
import { EcrApiError } from '@/api/client';
import {
  applyGate,
  buildOptions,
  confirmedOwnerKeys,
  EmptyDraft,
  fieldWithoutColumn,
  fileColumnOptions,
  fileStepProblem,
  sendFailureOf,
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
  it('профіль без специфікації; область сюди не входить (AIR-ON-AIR: окремі поля форми)', () => {
    expect(buildOptions(base)).toEqual({ profileCode: 'ERA_TABLE_33' });
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
  it('файл і розширення (область перевіряє крок «Region and area»)', () => {
    expect(fileStepProblem(EmptyDraft)).toBe('file');
    expect(fileStepProblem({ ...base, file: xlsx('a.xls') })).toBe('extension');
    expect(fileStepProblem({ ...base, region: '' })).toBeNull();
    expect(fileStepProblem(base)).toBeNull();
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

describe('поле без колонки і колонки файлу (P1 UAT 07.11)', () => {
  it('fieldWithoutColumn: the first field with neither a column nor header text', () => {
    expect(fieldWithoutColumn(withNewField({ fields: [{ name: 'NaeCode', patterns: ['N'] }] }, 'Workshop'))).toBe('Workshop');
    expect(fieldWithoutColumn({ fields: [{ name: 'A', column: 'B' }, { name: 'C', patterns: ['x'] }] })).toBeNull();
    expect(fieldWithoutColumn({ fields: [{ name: 'A', column: '  ', patterns: [] }] })).toBe('A');
  });

  it('fileColumnOptions: mapped and unmapped in file order, letter and header; a named letter past the header is kept', () => {
    const options = fileColumnOptions(
      [
        { column: 27, columnLetter: 'AA', headerText: 't/year' },
        { column: 2, columnLetter: 'B', headerText: 'Цех\n  (shop)' },
        { column: 7, columnLetter: 'G', headerText: '' },
      ],
      ['b', 'AZ', null],
    );
    expect(options).toEqual([
      { value: 'B', label: 'B — Цех (shop)' },
      { value: 'G', label: 'G' },
      { value: 'AA', label: 'AA — t/year' },
      { value: 'AZ', label: 'AZ' },
    ]);
  });

  it('sendFailureOf: a server answer is not a send failure; a TypeError is the network unless the file is unreadable', async () => {
    expect(await sendFailureOf(new EcrApiError({ title: 't', status: 422, errorCode: 'ECR-IMP-0422', correlationId: 'c' }), xlsx())).toBeNull();
    expect(await sendFailureOf(new SyntaxError('json'), xlsx())).toBeNull();
    expect(await sendFailureOf(new TypeError('Failed to fetch'), xlsx())).toBe('network');
    expect(await sendFailureOf(new TypeError('Failed to fetch'), null)).toBe('network');
    const changed = xlsx();
    Object.defineProperty(changed, 'slice', { value: () => ({}) });
    expect(await sendFailureOf(new TypeError('Failed to fetch'), changed)).toBe('fileUnreadable');
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
