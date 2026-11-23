import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, within } from '@testing-library/react';
import { Catalog } from '@/test/__tests__/a11yFixtures';
import { TestProviders } from '@/test/render';
import { RowChangesPanel } from '../import/RowChangesPanel';
import type { AirPreview } from '../airTypes';
import { preview } from './airFixtures';

/**
 * D-569 (P-1123-1, людина 23.11: «одна остання завантажена таблиця актуальна»): before the apply the preview lists exactly what the
 * new table replaces — per NAE / substance added, changed (field: old → new, an absent value as «(empty)») or removed (a row of
 * another region / area with its scope), the counts, and the columns the file does not have (empty after the apply).
 *
 * Mutation proofs (run locally, each restored): `RowChangesPanel` without the `fields` list fails «changed shows old → new»;
 * `emptied.length > 0 &&` → `false &&` fails «columns not in the file»; the `scope` note removed fails «other region / area».
 */
vi.mock('@/shared/i18n', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/i18n')>()),
  t: (key: string, params?: Record<string, string | number>) =>
    (Catalog[key] ?? `⟦${key}⟧`).replace(/\{(\w+)\}/g, (match, name: string) =>
      params !== undefined && name in params ? String(params[name]) : match,
    ),
}));

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

const text = (key: string): string => Catalog[key] ?? key;

function body(): AirPreview {
  return {
    ...preview({
      diff: { newParents: 1, changedParents: 1, unchangedParents: 0, removedParents: 1, skippedParents: 0, children: 3, newChildren: 2, changedChildren: 1, removedChildren: 1 },
      rows: [
        { kind: 'Added', key: '9304', childKey: null, scope: null, fields: [] },
        { kind: 'Added', key: '9304', childKey: '0333', scope: null, fields: [] },
        { kind: 'Changed', key: '9301', childKey: null, scope: null, fields: [{ field: 'FILE_B', old: 'WS-1', new: null }] },
        { kind: 'Changed', key: '9301', childKey: '2735', scope: null, fields: [{ field: 'tonsPerYear', old: '1', new: '1.5' }] },
        { kind: 'Removed', key: '9101', childKey: null, scope: 'REG_001 / OFFSHORE', fields: [] },
      ],
      rowsDropped: 0,
    }),
    emptiedColumns: [{ table: 'substances', code: 'NDV_YEAR', nameL10n: { en: 'NDV year' }, dataType: 'Int' }],
  };
}

describe('RowChangesPanel — what the new table replaces', () => {
  it('counts of sources and substance rows', () => {
    render(<RowChangesPanel preview={body()} />, { wrapper: TestProviders });

    expect(screen.getByTestId('air-changes-sources').textContent).toBe('Sources: 1 added, 1 changed, 1 removed');
    expect(screen.getByTestId('air-changes-substances').textContent).toBe('Substance rows: 2 added, 1 changed, 1 removed');
  });

  it('every row with its kind, NAE and substance; a changed row shows old → new and an absent value as «(empty)»', () => {
    render(<RowChangesPanel preview={body()} columnNames={new Map([['FILE_B', 'Цех']])} />, { wrapper: TestProviders });

    const rows = screen.getAllByTestId('air-row-change');
    expect(rows).toHaveLength(5);
    expect(rows[0]!.getAttribute('data-kind')).toBe('Added');
    expect(within(rows[0]!).getByText(text('permits.air.import.changes.wholeSource'))).toBeTruthy();
    expect(rows[2]!.textContent).toContain(`Цех: WS-1 → ${text('permits.air.import.changes.empty')}`);
    expect(rows[3]!.textContent).toContain('2735');
    expect(rows[3]!.textContent).toContain('1 → 1.5');
  });

  it('a removed row of another region / area names its scope', () => {
    render(<RowChangesPanel preview={body()} />, { wrapper: TestProviders });

    const removed = screen.getAllByTestId('air-row-change').find((row) => row.getAttribute('data-kind') === 'Removed')!;
    expect(removed.textContent).toContain('9101');
    expect(removed.textContent).toContain('REG_001 / OFFSHORE');
  });

  it('a row of ANOTHER permit the confirmed replace takes names that permit', () => {
    const withOther = body();
    const rows = [...(withOther.preview.rows ?? []), { kind: 'Removed' as const, key: '9301', childKey: null, scope: null, fields: [], ownerLabel: 'KZ46VCZ' }];
    render(<RowChangesPanel preview={{ ...withOther, preview: { ...withOther.preview, rows } }} />, { wrapper: TestProviders });

    const note = screen.getByTestId('air-row-change-other-permit');
    expect(note.textContent).toContain('KZ46VCZ');
    expect(note.closest('tr')?.getAttribute('data-kind')).toBe('Removed');
  });

  it('columns not in the file: a warning that they will be empty, with their names', () => {
    render(<RowChangesPanel preview={body()} />, { wrapper: TestProviders });

    const banner = screen.getByTestId('air-emptied-columns');
    expect(banner.textContent).toContain(text('permits.air.import.changes.emptiedTitle'));
    expect(banner.textContent).toContain('NDV year');
  });

  it('nothing to change: no table, the «no row changes» line; dropped rows are counted', () => {
    const quiet = { ...body(), emptiedColumns: [], preview: { ...body().preview, rows: [], rowsDropped: 7 } };
    render(<RowChangesPanel preview={quiet} />, { wrapper: TestProviders });

    expect(screen.queryByRole('table')).toBeNull();
    expect(screen.getByText(text('permits.air.import.changes.none'))).toBeTruthy();
    expect(screen.getByText('7 more changes are not listed.')).toBeTruthy();
    expect(screen.queryByTestId('air-emptied-columns')).toBeNull();
  });
});
