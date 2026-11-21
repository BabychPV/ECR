import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { Catalog } from '@/test/__tests__/a11yFixtures';
import { TestProviders } from '@/test/render';
import * as airApi from '@/features/permits/air/airApi';
import type { AirAnalysis } from '../airTypes';
import { AirImportWizard } from '../import/AirImportWizard';
import { MappingStep, type MappingStepProps } from '../import/MappingStep';
import { analysis, preview, profile, profiles, result, scopeCodes, scopeFields, tree, xlsx } from './airFixtures';
import { sampleWorkbook, withDecompression } from './xlsxZip';

/**
 * UX 20.11 (product owner: «the mapping interface looks a bit complicated; make it easier for the user») — the «Mapping» step
 * of «Import parameters…» as a non-technical user meets it: the common case is «check and continue»; a required field that was
 * not found asks for its column right in its row; the rules editor is still there under «Advanced»; a profile is saved with a
 * name only.
 *
 * Mutations (each run by hand in the worktree, red, restored):
 * - `MappingStep` `onPick` without `onApplySpec` (only `onSpec`) → «missing required field → pick its column» red (no re-read);
 * - `FieldsTable` picker shown for found rows without «Change» → «auto-matched → Next» red (a picker in every row);
 * - `AirImportWizard` `pending` back to `draft.spec !== analysedSpec` → «Edit mapping opened and nothing changed» red;
 * - `useColumnSamples` returning `null` → «examples from the file» red.
 * Review 20.11 (each red, restored): `noRows` without the missing-column check (M1); no «Move column?» confirmation (M2);
 * «Advanced» not forced open on a problem (M4); level note hidden; focus not moved after the re-read (L1); the old `clearable`
 * rule (L2); no pending verdict (L3); pickers on the delayed spinner state (L4).
 */
vi.mock('@/shared/i18n', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/i18n')>()),
  t: (key: string, params?: Record<string, string | number>) =>
    (Catalog[key] ?? `⟦${key}⟧`).replace(/\{(\w+)\}/g, (match, name: string) =>
      params !== undefined && name in params ? String(params[name]) : match,
    ),
}));
vi.mock('@/features/permits/air/airApi', () => ({
  listImportProfiles: vi.fn(),
  getImportProfile: vi.fn(),
  saveImportProfile: vi.fn(),
  analyseAirImport: vi.fn(),
  previewAirImport: vi.fn(),
  applyAirImport: vi.fn(),
  getNaeTree: vi.fn(),
  getAirScopeCodes: vi.fn(),
  getAirScopeFields: vi.fn(),
}));
vi.mock('@/features/permits/api/typesApi', () => ({
  getPermitType: vi.fn().mockRejectedValue(new Error('not needed')),
  openPermitTypeVersion: vi.fn(),
  replacePermitType: vi.fn(),
  publishPermitType: vi.fn(),
}));

const api = vi.mocked(airApi);
const text = (key: string): string => Catalog[key] ?? key;
const fill = (key: string, params: Record<string, string>): string =>
  text(key).replace(/\{(\w+)\}/g, (match, name: string) => params[name] ?? match);
const button = (key: string): HTMLButtonElement => screen.getByRole('button', { name: text(key) });
const NaeLabel = (): string => text('permits.air.import.field.naeCode');
const TonsLabel = (): string => text('permits.air.substances.ty');

/**
 * The file has no «t/year» header the profile knows: AA is listed as a column nobody reads, the required field is not found. As
 * the server answers it (review 20.11, M1): a required column missing is an error, and then NO row is read — empty stats.
 */
function missingTons(): AirAnalysis {
  return analysis({
    canContinue: false,
    stats: { parents: 0, children: 0, distinctChildKeys: 0, groupRows: 0, skippedRows: 0 },
    header: {
      headerFromRow: 4,
      headerToRow: 6,
      dataFromRow: 9,
      mapped: [{ column: 7, columnLetter: 'G', field: 'NaeCode', headerText: 'Source number', level: 'Parent' }],
      unmapped: [
        { column: 2, columnLetter: 'B', headerText: 'Shop', hasData: true },
        { column: 27, columnLetter: 'AA', headerText: 'Tonnes a year', hasData: true },
      ],
    },
    issues: [
      { severity: 'Error', messageKey: 'err.ECR-IMP-0422.regColumnMissing', field: 'TonsPerYear', args: { field: 'TonsPerYear' } },
    ],
  });
}

beforeEach(() => {
  api.listImportProfiles.mockResolvedValue(profiles);
  api.getImportProfile.mockResolvedValue(profile());
  api.analyseAirImport.mockResolvedValue(analysis());
  api.previewAirImport.mockResolvedValue(preview());
  api.applyAirImport.mockResolvedValue(result());
  api.getNaeTree.mockResolvedValue(tree());
  api.getAirScopeCodes.mockResolvedValue(scopeCodes());
  api.getAirScopeFields.mockResolvedValue(scopeFields());
});
afterEach(() => {
  cleanup();
  vi.clearAllMocks();
  vi.unstubAllGlobals();
});

async function pick(label: string | RegExp, option: RegExp): Promise<void> {
  await userEvent.click(screen.getByRole('textbox', { name: typeof label === 'string' ? new RegExp(`^${label}`) : label }));
  await userEvent.click(await screen.findByRole('option', { name: option }));
}

async function toMapping(): Promise<void> {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <TestProviders>
      <QueryClientProvider client={client}>
        <MemoryRouter>
          <AirImportWizard permitId={5} permitNumber="KZ87VCZ14622157" mayImport mayEditProfile />
        </MemoryRouter>
      </QueryClientProvider>
    </TestProviders>,
  );
  await screen.findByLabelText(new RegExp(text('permits.air.import.region.label')));
  await pick(text('permits.air.import.region.label'), /^Atyrau/);
  await pick(text('permits.air.import.area.label'), /^Offshore/);
  fireEvent.click(button('wizard.next'));
  await screen.findByRole('heading', { name: text('permits.air.import.step.file') });
  const input = document.querySelector<HTMLInputElement>('input[type="file"]');
  if (input === null) throw new Error('file input');
  await userEvent.upload(input, xlsx());
  fireEvent.click(button('permits.air.import.analyse'));
  await screen.findByTestId('air-header-report');
  // The profile is read for the step: its fields are the rows.
  await waitFor(() => expect(api.getImportProfile).toHaveBeenCalled());
}

describe('UX 20.11 — the common case: check and continue', () => {
  it('auto-matched: the verdict says all is found, each row is «Found» with its column, no picker, Next opens the preview', async () => {
    await toMapping();

    const verdict = screen.getByTestId('air-mapping-verdict');
    expect(verdict.getAttribute('data-tone')).toBe('success');
    expect(within(verdict).getByText(fill('permits.air.import.verdict.readyTitle', { found: '2', total: '2' }))).toBeTruthy();
    const table = screen.getByRole('region', { name: text('permits.air.import.map.mappedAria') });
    const rows = within(table).getAllByRole('row').slice(1);
    expect(rows.map((row) => within(row).getByRole('rowheader').textContent)).toEqual([NaeLabel(), TonsLabel()]);
    for (const row of rows) {
      expect(within(row).getByText(text('permits.air.import.fields.found'))).toBeTruthy();
      expect(within(row).getByText(text('permits.air.import.fields.required'))).toBeTruthy();
    }
    expect(within(rows[0]!).getByText('Source number')).toBeTruthy();
    // The level stays visible, in plain words and read-only.
    expect(within(rows[0]!).getByText(text('permits.air.import.fields.perSource'))).toBeTruthy();
    expect(within(rows[1]!).getByText(text('permits.air.import.fields.perRow'))).toBeTruthy();
    // Nothing to decide: no pickers, only «Change» per row; the technical parts are folded.
    expect(within(table).queryAllByRole('textbox')).toHaveLength(0);
    expect(within(table).getByRole('button', { name: fill('permits.air.import.fields.changeFor', { field: NaeLabel() }) })).toBeTruthy();
    expect(screen.getByTestId('air-advanced').hasAttribute('open')).toBe(false);
    expect(screen.getByTestId('air-layout').hasAttribute('open')).toBe(false);
    expect(screen.queryByTestId('air-next-blocked')).toBeNull();

    fireEvent.click(button('permits.air.import.toPreview'));
    await waitFor(() => expect(api.previewAirImport).toHaveBeenCalledTimes(1));
    expect(api.previewAirImport.mock.calls[0]![2]).toEqual({ profileCode: 'ERA_TABLE_33' });
  });

  it('«Change» on a found field: the picker takes the focus; the chosen column re-reads the file with it', async () => {
    await toMapping();
    fireEvent.click(screen.getByRole('button', { name: fill('permits.air.import.fields.changeFor', { field: NaeLabel() }) }));
    const picker = screen.getByRole('textbox', { name: fill('permits.air.import.fields.pickFor', { field: NaeLabel() }) });
    expect(document.activeElement).toBe(picker);
    await userEvent.click(picker);
    await userEvent.click(await screen.findByRole('option', { name: /^B — Shop/ }));

    await waitFor(() => expect(api.analyseAirImport).toHaveBeenCalledTimes(2));
    const options = api.analyseAirImport.mock.calls[1]![1];
    expect(options.spec?.fields?.[0]).toMatchObject({ name: 'NaeCode', column: 'B' });
    expect(options.profileCode).toBeUndefined();
  });

  it('«Edit mapping» opened and nothing changed: Next stays enabled (no re-read of the very same mapping)', async () => {
    await toMapping();
    fireEvent.click(screen.getByText(text('permits.air.import.advanced')));
    fireEvent.click(button('permits.air.import.map.edit'));
    await screen.findAllByLabelText(text('permits.air.import.map.fieldName'));

    expect(button('permits.air.import.toPreview').disabled).toBe(false);
    expect(screen.queryByText(text('permits.air.import.map.reanalyseFirst'))).toBeNull();
  });
});

describe('UX 20.11 — a required field was not found', () => {
  it('the verdict names the count; the row asks for its column with the reason; Next is disabled and says why', async () => {
    api.analyseAirImport.mockResolvedValueOnce(missingTons());
    await toMapping();

    const verdict = screen.getByTestId('air-mapping-verdict');
    expect(verdict.getAttribute('data-tone')).toBe('warning');
    expect(within(verdict).getByText(fill('permits.air.import.verdict.missingTitle', { count: '1' }))).toBeTruthy();
    const table = screen.getByRole('region', { name: text('permits.air.import.map.mappedAria') });
    const first = within(table).getAllByRole('row')[1]!;
    // What needs a decision comes first.
    expect(within(first).getByRole('rowheader').textContent).toBe(TonsLabel());
    expect(within(first).getByText(text('permits.air.import.fields.missing'))).toBeTruthy();
    expect(within(first).getByText(`The column for "${TonsLabel()}" was not found in the file header.`, { exact: false })).toBeTruthy();
    const picker = within(first).getByRole('textbox', { name: fill('permits.air.import.fields.pickFor', { field: TonsLabel() }) });
    expect(picker.getAttribute('aria-invalid')).toBe('true');
    expect(button('permits.air.import.toPreview').disabled).toBe(true);
    expect(screen.getByTestId('air-next-blocked').textContent).toBe(text('permits.air.import.map.blockedHint'));
    // M1: the empty stats are the missing column's consequence — no «No source was read», the layout stays folded.
    expect(screen.queryByTestId('air-no-rows-reason')).toBeNull();
    expect(screen.getByTestId('air-layout').hasAttribute('open')).toBe(false);
  });

  it('pick the column from the file\'s columns (header text, the field reading one now): the file is re-read and Next opens', async () => {
    api.analyseAirImport.mockResolvedValueOnce(missingTons());
    await toMapping();

    await userEvent.click(screen.getByRole('textbox', { name: fill('permits.air.import.fields.pickFor', { field: TonsLabel() }) }));
    const options = await screen.findAllByRole('option');
    expect(options.map((option) => option.textContent)).toEqual([
      'B — Shop',
      `G — Source number${fill('permits.air.import.fields.usedBy', { field: NaeLabel() })}`,
      'AA — Tonnes a year',
    ]);
    await userEvent.click(screen.getByRole('option', { name: /^AA — Tonnes a year/ }));

    await waitFor(() => expect(api.analyseAirImport).toHaveBeenCalledTimes(2));
    const sent = api.analyseAirImport.mock.calls[1]![1].spec;
    expect(sent?.fields?.[1]).toMatchObject({ name: 'TonsPerYear', column: 'AA', patterns: ['t/year'], required: true });
    expect(sent?.fields?.[0]).toEqual(profile().spec.fields?.[0]);
    await waitFor(() => expect(button('permits.air.import.toPreview').disabled).toBe(false));
    expect(screen.getByTestId('air-mapping-verdict').getAttribute('data-tone')).toBe('success');

    fireEvent.click(button('permits.air.import.toPreview'));
    await waitFor(() => expect(api.previewAirImport).toHaveBeenCalledTimes(1));
    expect(api.previewAirImport.mock.calls[0]![2].spec?.fields?.[1]?.column).toBe('AA');
  });

  it('no source read: the «Where the table is» disclosure opens by itself with the rows to fix', async () => {
    api.analyseAirImport.mockResolvedValueOnce(
      analysis({ canContinue: false, stats: { parents: 0, children: 0, distinctChildKeys: 0, groupRows: 0, skippedRows: 0 } }),
    );
    await toMapping();

    expect(screen.getByTestId('air-layout').hasAttribute('open')).toBe(true);
    expect(screen.getByTestId('air-no-rows-reason')).toBeTruthy();
    expect(screen.getByRole('textbox', { name: text('permits.air.import.rows.headerFrom') })).toBeTruthy();
  });
});

const changeFor = (field: string): HTMLButtonElement =>
  screen.getByRole('button', { name: fill('permits.air.import.fields.changeFor', { field }) });
const pickerFor = (field: string): HTMLElement => screen.getByRole('textbox', { name: fill('permits.air.import.fields.pickFor', { field }) });

/** A profile with a column added by «Add as new column»: it names its file column B and has no header texts. */
function withAddedColumn(): ReturnType<typeof profile> {
  const base = profile();

  return { ...base, spec: { fields: [...(base.spec.fields ?? []), { name: 'FILE_B', column: 'B', patterns: [], level: 'Parent', parser: 'Text' }] } };
}

function readsB(): AirAnalysis {
  return analysis({
    header: {
      headerFromRow: 4,
      headerToRow: 6,
      dataFromRow: 9,
      mapped: [
        { column: 2, columnLetter: 'B', field: 'FILE_B', headerText: 'Shop', level: 'Parent' },
        { column: 7, columnLetter: 'G', field: 'NaeCode', headerText: 'Source number', level: 'Parent' },
        { column: 27, columnLetter: 'AA', field: 'TonsPerYear', headerText: 't/year', level: 'Child' },
      ],
      unmapped: [],
    },
  });
}

describe('review 20.11 — taking a column another field reads (M2)', () => {
  it('asks first, says the consequence; «Move column» re-reads with the picked field before the owner that read it by header text', async () => {
    await toMapping();
    fireEvent.click(changeFor(TonsLabel()));
    await userEvent.click(pickerFor(TonsLabel()));
    await userEvent.click(await screen.findByRole('option', { name: /^G — Source number/ }));

    const ask = screen.getByTestId('air-column-move');
    expect(within(ask).getByText(fill('permits.air.import.fields.moveTitle', { column: 'G', owner: NaeLabel() }))).toBeTruthy();
    expect(within(ask).getByText(fill('permits.air.import.fields.moveText', { column: 'G', owner: NaeLabel(), field: TonsLabel() }))).toBeTruthy();
    expect(within(ask).getByText(fill('permits.air.import.fields.moveRequired', { owner: NaeLabel() }))).toBeTruthy();
    expect(document.activeElement).toBe(within(ask).getByRole('button', { name: text('permits.air.import.fields.move') }));
    expect(api.analyseAirImport).toHaveBeenCalledTimes(1);

    fireEvent.click(within(ask).getByRole('button', { name: text('permits.air.import.fields.move') }));
    await waitFor(() => expect(api.analyseAirImport).toHaveBeenCalledTimes(2));
    const sent = api.analyseAirImport.mock.calls[1]![1].spec;
    // NaeCode reads G by header text and comes first: the server would keep G for it — TonsPerYear goes before it.
    expect(sent?.fields?.map((field) => [field.name, field.column ?? null])).toEqual([
      ['TonsPerYear', 'G'],
      ['NaeCode', null],
    ]);
  });

  it('Cancel takes nothing: no re-read, the column stays with its owner', async () => {
    await toMapping();
    fireEvent.click(changeFor(TonsLabel()));
    await userEvent.click(pickerFor(TonsLabel()));
    await userEvent.click(await screen.findByRole('option', { name: /^G — Source number/ }));
    fireEvent.click(
      screen.getByRole('button', { name: fill('permits.air.import.fields.moveCancel', { column: 'G', owner: NaeLabel() }) }),
    );

    expect(screen.queryByTestId('air-column-move')).toBeNull();
    expect(api.analyseAirImport).toHaveBeenCalledTimes(1);
  });

  it('an owner that names its column only (added column): after the move it has none — said next to «Save as profile…», the editor opens', async () => {
    api.getImportProfile.mockResolvedValue(withAddedColumn());
    api.analyseAirImport.mockResolvedValueOnce(readsB());
    await toMapping();
    await screen.findByText('FILE_B');
    fireEvent.click(changeFor(NaeLabel()));
    await userEvent.click(pickerFor(NaeLabel()));
    await userEvent.click(await screen.findByRole('option', { name: /^B — Shop/ }));
    fireEvent.click(within(screen.getByTestId('air-column-move')).getByRole('button', { name: text('permits.air.import.fields.move') }));

    await waitFor(() => expect(api.analyseAirImport).toHaveBeenCalledTimes(2));
    const sent = api.analyseAirImport.mock.calls[1]![1].spec;
    expect(sent?.fields?.find((field) => field.name === 'FILE_B')?.column).toBeNull();
    expect(sent?.fields?.find((field) => field.name === 'NaeCode')?.column).toBe('B');
    const banner = await screen.findByTestId('air-field-without-column');
    expect(banner.textContent).toContain('Field "FILE_B" has no file column');
    expect(screen.getByTestId('air-advanced').contains(banner)).toBe(false);
    expect(screen.getByTestId('air-advanced').hasAttribute('open')).toBe(true);
    expect(button('permits.air.import.map.saveProfile').disabled).toBe(true);
  });
});

describe('review 20.11 — the smaller ones', () => {
  it('L1: after a pick and the re-read the focus is on that row\'s «Change»', async () => {
    api.analyseAirImport.mockResolvedValueOnce(missingTons());
    await toMapping();
    await userEvent.click(pickerFor(TonsLabel()));
    await userEvent.click(await screen.findByRole('option', { name: /^AA — Tonnes a year/ }));

    await waitFor(() => expect(document.activeElement).toBe(changeFor(TonsLabel())));
  });

  it('L2: «back to header texts» (clear) only for a field with an explicit column and header texts', async () => {
    api.getImportProfile.mockResolvedValue({
      ...profile(),
      spec: { fields: [{ ...profile().spec.fields![0]!, column: 'G' }, profile().spec.fields![1]!, ...(withAddedColumn().spec.fields ?? []).slice(2)] },
    });
    api.analyseAirImport.mockResolvedValueOnce(readsB());
    await toMapping();
    await screen.findByText('FILE_B');
    for (const field of [NaeLabel(), TonsLabel(), 'FILE_B']) fireEvent.click(changeFor(field));

    expect(screen.getByRole('button', { name: fill('permits.air.import.fields.clearFor', { field: NaeLabel() }) })).toBeTruthy();
    expect(screen.queryByRole('button', { name: fill('permits.air.import.fields.clearFor', { field: TonsLabel() }) })).toBeNull();
    expect(screen.queryByRole('button', { name: fill('permits.air.import.fields.clearFor', { field: 'FILE_B' }) })).toBeNull();
  });

  it('L3 + L4: while the file is re-read the verdict says the checks are of the previous reading, and the pickers wait', async () => {
    api.analyseAirImport.mockResolvedValueOnce(missingTons());
    await toMapping();
    let finish: (value: AirAnalysis) => void = () => undefined;
    api.analyseAirImport.mockImplementationOnce(() => new Promise<AirAnalysis>((resolve) => (finish = resolve)));
    await userEvent.click(pickerFor(TonsLabel()));
    await userEvent.click(await screen.findByRole('option', { name: /^AA — Tonnes a year/ }));

    await waitFor(() => expect(screen.getByTestId('air-mapping-verdict').getAttribute('data-tone')).toBe('info'));
    expect(screen.getByText(text('permits.air.import.verdict.pendingTitle'))).toBeTruthy();
    expect(changeFor(NaeLabel()).disabled).toBe(true);
    expect((pickerFor(TonsLabel()) as HTMLInputElement).disabled).toBe(true);

    finish(analysis());
    await waitFor(() => expect(screen.getByTestId('air-mapping-verdict').getAttribute('data-tone')).toBe('success'));
    expect(changeFor(NaeLabel()).disabled).toBe(false);
  });

  it('M4: an edit the profile cannot be saved with opens «Advanced» by itself; the reason stands next to the save button', async () => {
    await toMapping();
    fireEvent.click(button('permits.air.import.map.edit'));
    const names = await screen.findAllByLabelText(text('permits.air.import.map.fieldName'));
    expect(screen.getByTestId('air-advanced').hasAttribute('open')).toBe(false);
    fireEvent.change(names[1]!, { target: { value: 'naecode' } });

    const banner = await screen.findByTestId('air-mapping-problem');
    expect(within(banner).getByText(text('permits.air.import.map.nameDuplicate'))).toBeTruthy();
    expect(screen.getByTestId('air-advanced').contains(banner)).toBe(false);
    expect(screen.getByTestId('air-advanced').hasAttribute('open')).toBe(true);
    expect(button('permits.air.import.map.saveProfile').disabled).toBe(true);
  });
});

describe('UX 20.11 — nothing was taken away', () => {
  it('«Advanced» still edits the rules, in plain words: level as «One value per», parser as «Value type»; re-analyse sends them', async () => {
    await toMapping();
    fireEvent.click(screen.getByText(text('permits.air.import.advanced')));
    fireEvent.click(button('permits.air.import.map.edit'));
    const editor = await screen.findByRole('region', { name: text('permits.air.import.map.editorAria') });
    expect(within(editor).getByRole('columnheader', { name: text('permits.air.import.map.level') })).toBeTruthy();
    expect(within(editor).getByRole('columnheader', { name: text('permits.air.import.map.parser') })).toBeTruthy();

    await pick('Level of field TonsPerYear', new RegExp(`^${text('permits.air.import.map.level.parent')}$`));
    await pick('Parser of field TonsPerYear', new RegExp(`^${text('permits.air.import.add.kind.whole')}$`));
    fireEvent.change(screen.getByLabelText('Header patterns of field TonsPerYear'), { target: { value: 't/year | Tonnes a year' } });
    expect(button('permits.air.import.toPreview').disabled).toBe(true);

    fireEvent.click(button('permits.air.import.map.reanalyse'));
    await waitFor(() => expect(api.analyseAirImport).toHaveBeenCalledTimes(2));
    expect(api.analyseAirImport.mock.calls[1]![1].spec?.fields?.[1]).toMatchObject({
      level: 'Parent',
      parser: 'Integer',
      patterns: ['t/year', 'Tonnes a year'],
    });
  });

  it('a profile is saved with a name only: the generated code goes to the server, the wizard goes on with it', async () => {
    api.saveImportProfile.mockResolvedValue({ ...profile(), code: 'SIMILAR_FILES_2026', builtIn: false });
    await toMapping();

    fireEvent.click(button('permits.air.import.map.saveProfile'));
    fireEvent.change(screen.getByLabelText(text('permits.air.import.save.name')), { target: { value: 'Similar files 2026' } });
    fireEvent.click(button('permits.air.import.save.submit'));
    await waitFor(() => expect(api.saveImportProfile).toHaveBeenCalledTimes(1));
    expect(api.saveImportProfile.mock.calls[0]![0]).toBe('SIMILAR_FILES_2026');
    expect(api.saveImportProfile.mock.calls[0]![1].nameL10n['en']).toBe('Similar files 2026');
    expect(await screen.findByText('Profile SIMILAR_FILES_2026 saved and selected.')).toBeTruthy();
    expect(button('permits.air.import.toPreview').disabled).toBe(false);
  });
});

function mountStep(patch: Partial<MappingStepProps> = {}): void {
  render(
    <TestProviders>
      <MappingStep
        analysis={analysis()}
        profile={profile()}
        spec={null}
        onSpec={vi.fn()}
        onReanalyse={vi.fn()}
        reanalysing={false}
        mayEditProfile
        onSaveProfile={vi.fn()}
        saving={false}
        savedCode={null}
        onApplySpec={vi.fn()}
        mayAddColumn={false}
        onAddColumn={vi.fn()}
        addingColumn={false}
        {...patch}
      />
    </TestProviders>,
  );
}

describe('UX 20.11 — examples from the file', () => {
  it('values under each column are read from the chosen workbook: in the table and next to a column nobody reads', async () => {
    withDecompression();
    mountStep({ file: sampleWorkbook(), analysis: analysis({ sheetName: 'Table 3.3', sheetNames: ['Summary', 'Table 3.3'] }) });

    const table = screen.getByRole('region', { name: text('permits.air.import.map.mappedAria') });
    expect(await within(table).findByRole('columnheader', { name: text('permits.air.import.fields.examples') })).toBeTruthy();
    const rows = within(table).getAllByRole('row').slice(1);
    expect(within(rows[0]!).getByText('1 · 2 · 3')).toBeTruthy();
    expect(within(rows[1]!).getByText('0.25 · 1.5')).toBeTruthy();
    const unmapped = screen.getByTestId('air-unmapped');
    expect(within(unmapped).getByText(fill('permits.air.import.map.samples', { values: 'Цех 1 · Цех 2 · A & B' }))).toBeTruthy();
  });

  it('a file that cannot be read gives no «Examples» column, and the step works as before', async () => {
    withDecompression();
    mountStep({ file: xlsx() });

    const table = screen.getByRole('region', { name: text('permits.air.import.map.mappedAria') });
    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(within(table).queryByRole('columnheader', { name: text('permits.air.import.fields.examples') })).toBeNull();
    expect(within(table).getAllByRole('row')).toHaveLength(3);
  });

  it('L4: the pickers wait for the reading itself (`reading`), not for the delayed spinner state', () => {
    mountStep({ analysis: missingTons(), reanalysing: false, reading: true });
    expect((pickerFor(TonsLabel()) as HTMLInputElement).disabled).toBe(true);
    expect(changeFor(NaeLabel()).disabled).toBe(true);
    cleanup();
    mountStep({ analysis: missingTons(), reanalysing: false, reading: false });
    expect((pickerFor(TonsLabel()) as HTMLInputElement).disabled).toBe(false);
  });

  it('optional fields the file does not have are folded below the table, still with a picker each', () => {
    const withOptional = { ...profile(), spec: { fields: [...(profile().spec.fields ?? []), { name: 'Height', patterns: ['Height'], level: 'Parent' as const }] } };
    mountStep({ profile: withOptional });

    const absent = screen.getByTestId('air-fields-absent');
    expect(absent.hasAttribute('open')).toBe(false);
    expect(within(absent).getByText(fill('permits.air.import.fields.absent', { count: '1' }))).toBeTruthy();
    expect(within(absent).getByText(text('permits.air.import.fields.notInFile'))).toBeTruthy();
    expect(within(absent).getByRole('textbox', { name: fill('permits.air.import.fields.pickFor', { field: text('permits.air.sources.height') }) })).toBeTruthy();
  });
});
