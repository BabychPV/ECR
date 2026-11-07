import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { Catalog } from '@/test/__tests__/a11yFixtures';
import { TestProviders } from '@/test/render';
import * as typesApi from '@/features/permits/api/typesApi';
import type { PermitType, PermitTypeColumn } from '@/features/permits/api/types';
import {
  AddColumnError,
  addPermitTableColumn,
  columnCode,
  fieldFor,
  newColumn,
  withColumnField,
  type NewColumnRequest,
} from '../import/addColumn';
import { headerRowsValid } from '../import/LayoutControls';
import { MappingStep, withHeaderRows, type MappingStepProps } from '../import/MappingStep';
import { analysis, profile } from './airFixtures';

/**
 * P1 06.11: the AIR import wizard on a real-shaped file — header rows chosen by the user (auto-detected default), and
 * «Add as new column» for a file column no field reads (a new column of the permit table through the permit-type API).
 *
 * Mutations (run, red): `addPermitTableColumn` without the `hasUnpublishedChanges` check — «someone else's draft is refused»;
 * `columnCode` without the reuse branch — «the same column again adds nothing»; `withHeaderRows` without `dataStartRow` —
 * «re-read with the chosen rows»; 07.11 `withHeaderRows` without `...sheet` — «the chosen sheet goes into the spec» (red, restored).
 */
vi.mock('@/shared/i18n', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/i18n')>()),
  t: (key: string, params?: Record<string, string | number>) =>
    (Catalog[key] ?? `⟦${key}⟧`).replace(/\{(\w+)\}/g, (match, name: string) =>
      params !== undefined && name in params ? String(params[name]) : match,
    ),
}));
vi.mock('@/features/permits/api/typesApi', () => ({
  getPermitType: vi.fn(),
  openPermitTypeVersion: vi.fn(),
  replacePermitType: vi.fn(),
  publishPermitType: vi.fn(),
}));

const api = vi.mocked(typesApi);
const text = (key: string): string => Catalog[key] ?? key;

function column(code: string, name: string, order: number): PermitTypeColumn {
  return newColumn(code, { letter: 'B', name, kind: 'text', level: 'Parent' }, order);
}

function airType(patch: Partial<PermitType> = {}): PermitType {
  return {
    code: 'AIR',
    category: 'Air',
    nameL10n: { en: 'Air emission permit' },
    status: 'Published',
    version: 3,
    draftVersion: null,
    hasUnpublishedChanges: false,
    publishedAt: '2026-11-01T00:00:00Z',
    updatedAt: '2026-11-01T00:00:00Z',
    rowVersion: 'rv-published',
    headerFields: [],
    rules: [],
    tables: [
      { code: 'AIR_SOURCES', nameL10n: { en: 'Sources' }, order: 1, rules: [], columns: [column('SOURCE_NAME', 'Source name', 1)] },
      { code: 'AIR_SOURCE_SUBSTANCES', nameL10n: { en: 'Substances' }, order: 2, rules: [], columns: [column('TONS_PER_YEAR', 't/year', 1)] },
    ],
    ...patch,
  };
}

const Workshop: NewColumnRequest = { letter: 'B', name: 'Workshop', kind: 'text', level: 'Parent' };

beforeEach(() => {
  api.getPermitType.mockResolvedValue(airType());
  api.openPermitTypeVersion.mockResolvedValue(airType({ hasUnpublishedChanges: true, rowVersion: 'rv-draft', draftVersion: 4 }));
  api.replacePermitType.mockImplementation((_code, body) => Promise.resolve(airType({ tables: body.tables, rowVersion: 'rv-replaced' })));
  api.publishPermitType.mockResolvedValue(airType({ version: 4 }));
});
afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

describe('addColumn — the permit table column and the profile field', () => {
  it('code FILE_<letter>; a taken code gets a suffix; the same name reuses it', () => {
    expect(columnCode([], 'b', 'Workshop')).toEqual({ code: 'FILE_B', reuse: false });
    expect(columnCode([column('FILE_B', 'Other', 1)], 'B', 'Workshop')).toEqual({ code: 'FILE_B_2', reuse: false });
    expect(columnCode([column('FILE_B', 'Other', 1), column('FILE_B_2', 'Workshop', 2)], 'B', ' Workshop ')).toEqual({
      code: 'FILE_B_2',
      reuse: true,
    });
  });

  it('the type of the column and the parser of the field go together', () => {
    expect(newColumn('C', { ...Workshop, kind: 'number' }, 5)).toMatchObject({ dataType: 'Decimal', precision: 28, scale: 10, order: 5, key: false });
    expect(newColumn('C', { ...Workshop, kind: 'whole' }, 5)).toMatchObject({ dataType: 'Int', precision: null });
    expect(fieldFor('FILE_AB', { letter: 'ab', name: 'Year', kind: 'whole', level: 'Child' })).toEqual({
      name: 'FILE_AB',
      level: 'Child',
      column: 'AB',
      patterns: [],
      parser: 'Integer',
    });
    const spec = withColumnField(profile().spec, fieldFor('FILE_B', Workshop));
    expect(withColumnField(spec, fieldFor('FILE_B', { ...Workshop, kind: 'date' })).fields?.filter((f) => f.name === 'FILE_B')).toEqual([
      expect.objectContaining({ parser: 'Date' }),
    ]);
  });

  it('new version → replace with the column → publish with a reason', async () => {
    const code = await addPermitTableColumn(Workshop, 'Column «Workshop» added from import file column B');

    expect(code).toBe('FILE_B');
    expect(api.openPermitTypeVersion).toHaveBeenCalledWith('AIR');
    const [, body] = api.replacePermitType.mock.calls[0]!;
    expect(body.rowVersion).toBe('rv-draft');
    const sources = body.tables.find((table) => table.code === 'AIR_SOURCES')!;
    expect(sources.columns.map((c) => c.code)).toEqual(['SOURCE_NAME', 'FILE_B']);
    expect(sources.columns[1]).toMatchObject({ order: 2, dataType: 'String', nameL10n: { en: 'Workshop', ru: 'Workshop' } });
    // The other table is sent back unchanged.
    expect(body.tables.find((table) => table.code === 'AIR_SOURCE_SUBSTANCES')!.columns).toHaveLength(1);
    expect(api.publishPermitType).toHaveBeenCalledWith('AIR', { reason: 'Column «Workshop» added from import file column B', rowVersion: 'rv-replaced' });
  });

  it('someone else\'s unpublished draft is refused: nothing is opened or published', async () => {
    api.getPermitType.mockResolvedValue(airType({ hasUnpublishedChanges: true }));

    await expect(addPermitTableColumn(Workshop, 'reason long enough')).rejects.toEqual(new AddColumnError('draftOpen'));
    expect(api.openPermitTypeVersion).not.toHaveBeenCalled();
    expect(api.publishPermitType).not.toHaveBeenCalled();
  });

  it('the same column again adds nothing (a re-import of the same file)', async () => {
    api.getPermitType.mockResolvedValue(
      airType({
        hasUnpublishedChanges: true,
        tables: [{ code: 'AIR_SOURCES', nameL10n: { en: 'Sources' }, order: 1, rules: [], columns: [column('FILE_B', 'Workshop', 1)] }],
      }),
    );

    await expect(addPermitTableColumn(Workshop, 'reason long enough')).resolves.toBe('FILE_B');
    expect(api.openPermitTypeVersion).not.toHaveBeenCalled();
    expect(api.replacePermitType).not.toHaveBeenCalled();
  });
});

function mount(patch: Partial<MappingStepProps> = {}): MappingStepProps {
  const props: MappingStepProps = {
    analysis: analysis(),
    profile: profile(),
    spec: null,
    onSpec: vi.fn(),
    onReanalyse: vi.fn(),
    reanalysing: false,
    mayEditProfile: true,
    onSaveProfile: vi.fn(),
    saving: false,
    savedCode: null,
    onApplySpec: vi.fn(),
    mayAddColumn: true,
    onAddColumn: vi.fn(),
    addingColumn: false,
    ...patch,
  };
  render(
    <TestProviders>
      <MappingStep {...props} />
    </TestProviders>,
  );

  return props;
}

describe('MappingStep — where the table is, and «Add as new column»', () => {
  it('the detected rows are the default; changed rows re-read the file with the profile spec', () => {
    const props = mount();

    const from = screen.getByRole<HTMLInputElement>('textbox', { name: text('permits.air.import.rows.headerFrom') });
    const data = screen.getByRole<HTMLInputElement>('textbox', { name: text('permits.air.import.rows.dataFrom') });
    expect(from.value).toBe('4');
    expect(data.value).toBe('9');

    fireEvent.change(from, { target: { value: '5' } });
    fireEvent.change(data, { target: { value: '10' } });
    fireEvent.click(screen.getByRole('button', { name: text('permits.air.import.rows.apply') }));

    expect(props.onApplySpec).toHaveBeenCalledWith({ ...profile().spec, header: { fromRow: 5, toRow: 6 }, dataStartRow: 10 });
  });

  it('rows out of order are not sent', () => {
    const props = mount();

    fireEvent.change(screen.getByRole('textbox', { name: text('permits.air.import.rows.dataFrom') }), { target: { value: '6' } });
    fireEvent.click(screen.getByRole('button', { name: text('permits.air.import.rows.apply') }));

    expect(screen.getByRole('alert').textContent).toBe(text('permits.air.import.rows.invalid'));
    expect(props.onApplySpec).not.toHaveBeenCalled();
    expect(headerRowsValid({ headerFrom: 4, headerTo: 6, dataFrom: 7 })).toBe(true);
    expect(headerRowsValid({ headerFrom: 4, headerTo: 6, dataFrom: 6 })).toBe(false);
  });

  it('an unmapped column becomes a new column: name from the header, type and level chosen', () => {
    const props = mount();

    fireEvent.click(screen.getByRole('button', { name: 'Add file column B as a new column' }));
    expect(screen.getByRole<HTMLInputElement>('textbox', { name: text('permits.air.import.add.name') }).value).toBe('Shop');
    fireEvent.change(screen.getByRole('textbox', { name: text('permits.air.import.add.name') }), { target: { value: 'Workshop' } });
    fireEvent.click(screen.getByRole('button', { name: text('permits.air.import.add.submit') }));

    expect(props.onAddColumn).toHaveBeenCalledWith({ letter: 'B', name: 'Workshop', kind: 'text', level: 'Parent' });
  });

  it('without the rights to change the permit type there is no «Add as new column»', () => {
    mount({ mayAddColumn: false });

    expect(screen.getByText('Shop', { exact: false })).not.toBeNull();
    expect(screen.queryByRole('button', { name: 'Add file column B as a new column' })).toBeNull();
  });

  it('withHeaderRows keeps the fields and sets the rows', () => {
    const spec = withHeaderRows(profile().spec, { headerFrom: 4, headerTo: 6, dataFrom: 8 });

    expect(spec.fields).toEqual(profile().spec.fields);
    expect(spec.header).toEqual({ fromRow: 4, toRow: 6 });
    expect(spec.dataStartRow).toBe(8);
  });

  it('withHeaderRows: the chosen sheet goes into the spec; another sheet with untouched rows drops the explicit rows', () => {
    const base = { ...profile().spec, header: { fromRow: 4, toRow: 6 }, dataStartRow: 9 };

    expect(withHeaderRows(base, { headerFrom: 5, headerTo: 6, dataFrom: 9, sheet: 'Table 3.3' })).toEqual({
      ...base,
      sheet: 'Table 3.3',
      header: { fromRow: 5, toRow: 6 },
    });
    expect(withHeaderRows(base, { headerFrom: 4, headerTo: 6, dataFrom: 9, sheet: 'Summary', rowsAuto: true })).toEqual({
      fields: profile().spec.fields,
      sheet: 'Summary',
    });
  });

  it('the sheet choice shows only for a workbook of several sheets', () => {
    mount({ analysis: analysis({ sheetName: 'B', sheetNames: ['A', 'B'] }) });
    expect(screen.getByRole('textbox', { name: text('permits.air.import.rows.sheet') })).not.toBeNull();
    cleanup();
    mount();
    expect(screen.queryByRole('textbox', { name: text('permits.air.import.rows.sheet') })).toBeNull();
  });

  it('«Save as profile…» is one button outside «Advanced», also without edits; the form suggests the code from the name', () => {
    const props = mount();

    expect(screen.getAllByRole('button', { name: text('permits.air.import.map.saveProfile') })).toHaveLength(1);
    fireEvent.click(screen.getByRole('button', { name: text('permits.air.import.map.saveProfile') }));
    fireEvent.change(screen.getByRole('textbox', { name: text('permits.air.import.save.name') }), { target: { value: 'ERA 3.3 (Atyrau) v2' } });
    expect(screen.getByRole<HTMLInputElement>('textbox', { name: text('permits.air.import.save.code') }).value).toBe('ERA_3_3_ATYRAU_V2');
    fireEvent.click(screen.getByRole('button', { name: text('permits.air.import.save.submit') }));
    expect(props.onSaveProfile).toHaveBeenCalledWith('ERA_3_3_ATYRAU_V2', 'ERA 3.3 (Atyrau) v2');
  });

  it('a Cyrillic name gives no code: the code must be entered, nothing is saved before', () => {
    const props = mount();

    fireEvent.click(screen.getByRole('button', { name: text('permits.air.import.map.saveProfile') }));
    fireEvent.change(screen.getByRole('textbox', { name: text('permits.air.import.save.name') }), { target: { value: 'Похожие файлы' } });
    fireEvent.click(screen.getByRole('button', { name: text('permits.air.import.save.submit') }));
    expect(screen.getByText(text('permits.air.import.save.codeInvalid'))).not.toBeNull();
    expect(props.onSaveProfile).not.toHaveBeenCalled();
    fireEvent.change(screen.getByRole('textbox', { name: text('permits.air.import.save.code') }), { target: { value: 'similar' } });
    fireEvent.click(screen.getByRole('button', { name: text('permits.air.import.save.submit') }));
    expect(props.onSaveProfile).toHaveBeenCalledWith('SIMILAR', 'Похожие файлы');
  });
});
