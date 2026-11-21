import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, within } from '@testing-library/react';
import { Catalog } from '@/test/__tests__/a11yFixtures';
import { TestProviders } from '@/test/render';
import type { ImportIssue, ImportSpec } from '../airTypes';
import { fieldRows, type MatchedColumn } from '../import/fieldRows';
import { FieldsTable } from '../import/FieldsTable';

vi.mock('@/shared/i18n', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/i18n')>()),
  t: (key: string, params?: Record<string, string | number>) =>
    (Catalog[key] ?? `⟦${key}⟧`).replace(/\{(\w+)\}/g, (match, name: string) =>
      params !== undefined && name in params ? String(params[name]) : match,
    ),
}));

afterEach(cleanup);

const spec: ImportSpec = {
  fields: [
    { name: 'NaeCode', patterns: ['Source number'], level: 'Parent', parser: 'Code', required: true },
    { name: 'Height', patterns: ['Height'], level: 'Parent', parser: 'Decimal' },
  ],
};
const mapped = (field: string, column: number, letter: string): MatchedColumn => ({ field, column, columnLetter: letter, headerText: field, level: 'Parent' });
const invalid = (row: number): ImportIssue => ({
  severity: 'Error',
  messageKey: 'err.ECR-IMP-0422.regValueInvalid',
  row,
  field: 'Height',
  args: { field: 'Height', value: 'Цех 1' },
});

describe('FieldsTable — a mapped column whose values cannot be read', () => {
  it('the row says «Values not readable (N)» instead of «Found»; a clean row still says «Found»', () => {
    const rows = fieldRows(spec, [mapped('NaeCode', 7, 'G'), mapped('Height', 30, 'AD')], [invalid(3), invalid(4)]);
    render(
      <TestProviders>
        <FieldsTable rows={rows} columns={[]} samples={null} columnNames={new Map()} mayPick busy={false} onPick={() => undefined} />
      </TestProviders>,
    );

    const table = screen.getByTestId('air-fields');
    const height = within(table).getAllByRole('row').find((row) => within(row).queryByText('Height, m') !== null)!;
    const unreadable = within(height).getByText('Values not readable (2)');
    expect(unreadable.getAttribute('data-state')).toBe('unreadable');
    expect(within(height).queryByText(Catalog['permits.air.import.fields.found'] ?? '')).toBeNull();
    const code = within(table).getAllByRole('row').find((row) => within(row).queryByText('Source number') !== null || within(row).queryByText('NaeCode') !== null)!;
    expect(within(code).getByText(Catalog['permits.air.import.fields.found'] ?? '')).toBeTruthy();
  });
});
