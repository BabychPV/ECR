import { describe, expect, it, vi } from 'vitest';
import type { ImportIssue } from '../airTypes';
import { issueMessage } from '../import/fieldLabels';

vi.mock('@/shared/i18n', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/i18n')>()),
  t: (key: string, params?: Record<string, string | number>) =>
    (key === 'err.ECR-IMP-0422.regValueInvalid'
      ? '"{field}": the value "{value}" cannot be read.'
      : key === 'permits.air.sources.height'
        ? 'Height, m'
        : key
    ).replace(/\{(\w+)\}/g, (match, name: string) => (params !== undefined && name in params ? String(params[name]) : match)),
}));

/** The server puts the internal field key into `{field}`; the user reads the field's label everywhere else in the step. */
describe('issueMessage', () => {
  it('replaces the internal field key by the field label, keeps the other placeholders', () => {
    const issue: ImportIssue = {
      severity: 'Error',
      messageKey: 'err.ECR-IMP-0422.regValueInvalid',
      row: 5,
      field: 'height',
      args: { field: 'height', value: 'Цех 1' },
    };

    expect(issueMessage(issue)).toBe('"Height, m": the value "Цех 1" cannot be read.');
  });

  it('a field added with «Add as new column» takes the name of its column; an unknown key stays as it is', () => {
    const issue = (field: string): ImportIssue => ({ severity: 'Error', messageKey: 'err.ECR-IMP-0422.regValueInvalid', args: { field, value: 'x' } });
    const names = new Map([['FILE_B', 'Shop']]);

    expect(issueMessage(issue('FILE_B'), names)).toBe('"Shop": the value "x" cannot be read.');
    expect(issueMessage(issue('Whatever'), names)).toBe('"Whatever": the value "x" cannot be read.');
  });
});
