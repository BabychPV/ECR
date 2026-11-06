import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { EcrApiError } from '@/api/client';
import { Catalog } from '@/test/__tests__/a11yFixtures';
import { TestProviders } from '@/test/render';
import * as airApi from '@/features/permits/air/airApi';
import * as typesApi from '@/features/permits/api/typesApi';
import type { PermitType } from '@/features/permits/api/types';
import { AirImportWizard } from '../import/AirImportWizard';
import { EmptyDraft } from '../import/importModel';
import { PreviewStep } from '../import/PreviewStep';
import { airPermitType, analysis, preview, profile, profiles, result, scopeCodes, scopeFields, tree, xlsx } from './airFixtures';

/**
 * Майстер імпорту лімітів із Excel (NAE-план N5): файл -> відповідність -> перегляд -> застосування. API замоканий;
 * тексти - справжні з `09-seed.sql`.
 *
 * Мутаційні докази (виконано вручну, кожну мутацію відновлено):
 * - `applyGate`: прибрано `confirm` валить «без підтвердження заміни чужих дозволів застосувати не можна» (тут і в importModel);
 * - `buildOptions`: `expectedFingerprint` не передається валить «apply несе відбиток і власників»;
 * - `previewSignature`: рішення виключені з підпису валить «рішення про НАЕ застарюють перегляд»;
 * - `AirImportWizard.runApply`: прибрано `setForceStale(true)` валить «відмова apply вимагає нового перегляду».
 * - 07.11 `withHeaderRows`: прибрано `...sheet` (аркуш не йде в специфікацію) валить «одна форма збереження: профіль
 *   несе аркуш, рядки шапки, рядок даних і додане поле» і «вибір аркуша перечитує файл зі spec.sheet» (обидва червоні,
 *   відновлено).
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
  getPermitType: vi.fn(),
  openPermitTypeVersion: vi.fn(),
  replacePermitType: vi.fn(),
  publishPermitType: vi.fn(),
}));

const api = vi.mocked(airApi);
const types = vi.mocked(typesApi);
/** The AIR type as the server holds it: «Add as new column» changes it, the labels read it. */
let airType: PermitType = airPermitType();
const text = (key: string): string => Catalog[key] ?? key;

beforeEach(() => {
  api.listImportProfiles.mockResolvedValue(profiles);
  api.getImportProfile.mockResolvedValue(profile());
  api.analyseAirImport.mockResolvedValue(analysis());
  api.previewAirImport.mockResolvedValue(preview());
  api.applyAirImport.mockResolvedValue(result());
  api.getNaeTree.mockResolvedValue(tree());
  api.getAirScopeCodes.mockResolvedValue(scopeCodes());
  api.getAirScopeFields.mockResolvedValue(scopeFields());
  airType = airPermitType();
  types.getPermitType.mockImplementation(() => Promise.resolve(airType));
  types.openPermitTypeVersion.mockImplementation(() => Promise.resolve({ ...airType, rowVersion: 'rv-draft' }));
  types.replacePermitType.mockImplementation((_code, body) => {
    airType = { ...airType, tables: body.tables, rowVersion: 'rv-replaced' };
    return Promise.resolve(airType);
  });
  types.publishPermitType.mockImplementation(() => Promise.resolve(airType));
});
afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

function mount(props: { mayImport?: boolean; mayEditProfile?: boolean; mayAddColumn?: boolean } = {}): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <TestProviders>
      <QueryClientProvider client={client}>
        <MemoryRouter>
          <AirImportWizard
            permitId={5}
            permitNumber="KZ87VCZ14622157"
            mayImport={props.mayImport ?? true}
            mayEditProfile={props.mayEditProfile ?? true}
            mayAddColumn={props.mayAddColumn ?? false}
          />
        </MemoryRouter>
      </QueryClientProvider>
    </TestProviders>,
  );
}

const button = (key: string): HTMLButtonElement => screen.getByRole('button', { name: text(key) });

/** Mantine Select: open by its label, choose the option by its text. */
async function pick(label: string, option: RegExp): Promise<void> {
  await userEvent.click(screen.getByRole('textbox', { name: new RegExp(`^${label}`) }));
  await userEvent.click(await screen.findByRole('option', { name: option }));
}

/** Крок 1 «Регіон і ділянка» → «Next» → крок «Файл» із книгою. */
async function fillFile(region = 'Atyrau', area = 'Offshore'): Promise<void> {
  await screen.findByLabelText(new RegExp(text('permits.air.import.region.label')));
  await pick(text('permits.air.import.region.label'), new RegExp(`^${region}`));
  await pick(text('permits.air.import.area.label'), new RegExp(`^${area}`));
  fireEvent.click(button('wizard.next'));
  await screen.findByRole('heading', { name: text('permits.air.import.step.file') });
  const input = document.querySelector<HTMLInputElement>('input[type="file"]');
  if (input === null) throw new Error('file input');
  await userEvent.upload(input, xlsx());
}

async function toMapping(props: Parameters<typeof mount>[0] = {}): Promise<void> {
  mount(props);
  await fillFile();
  fireEvent.click(button('permits.air.import.analyse'));
  await screen.findByTestId('air-header-report');
}

async function toPreview(): Promise<void> {
  await toMapping();
  fireEvent.click(button('permits.air.import.toPreview'));
  await screen.findByText(text('permits.air.import.replace.title'));
}

describe('кроки 1-2: регіон і ділянка, файл', () => {
  it('без регіону далі не йде; без файлу аналіз не шле нічого', async () => {
    mount();
    expect(await screen.findByDisplayValue('KZ87VCZ14622157')).toBeTruthy();
    await screen.findByLabelText(new RegExp(text('permits.air.import.region.label')));
    fireEvent.click(button('wizard.next'));
    expect(await screen.findByText(text('permits.air.import.region.required'))).toBeTruthy();
    await pick(text('permits.air.import.region.label'), /^Atyrau/);
    fireEvent.click(button('wizard.next'));
    await screen.findByRole('heading', { name: text('permits.air.import.step.file') });
    fireEvent.click(button('permits.air.import.analyse'));
    expect(await screen.findByText(text('permits.air.import.file.required'))).toBeTruthy();
    expect(api.analyseAirImport).not.toHaveBeenCalled();
  });

  it('аналіз шле файл і options без області (область - лише полями форми preview/apply)', async () => {
    await toMapping();
    expect(api.analyseAirImport).toHaveBeenCalledTimes(1);
    const [file, options] = api.analyseAirImport.mock.calls[0]!;
    expect(file.name).toBe('limits.xlsx');
    expect(options).toEqual({ profileCode: 'ERA_TABLE_33' });
  });

  it('preview шле regionEntryId/areaEntryId (Registry) окремими полями', async () => {
    await toPreview();
    expect(api.previewAirImport.mock.calls[0]![3]).toEqual({ regionEntryId: '11', areaEntryId: '21' });
  });

  it('лише для читання: «Next» вимкнено', async () => {
    mount({ mayImport: false });
    await screen.findByLabelText(new RegExp(text('permits.air.import.region.label')));
    expect(button('wizard.next').disabled).toBe(true);
  });
});

describe('крок 2: відповідність', () => {
  it('звіт про шапку, змаплені й незмаплені колонки', async () => {
    await toMapping();
    expect(screen.getByTestId('air-header-report').textContent).toBe(
      'Header rows 4–6 · data from row 9 · group rows 27 · skipped rows 5',
    );
    const mapped = screen.getByRole('region', { name: text('permits.air.import.map.mappedAria') });
    expect(within(mapped).getByText('AA')).toBeTruthy();
    expect(screen.getByText('Unmapped columns: 1')).toBeTruthy();
  });

  it('редагування: шаблон змінено -> «Next: preview» вимкнено, доки файл не розібрано заново; аналіз шле spec', async () => {
    await toMapping();
    fireEvent.click(await screen.findByRole('button', { name: text('permits.air.import.map.edit') }));
    const patterns = await screen.findByLabelText('Header patterns of field TonsPerYear');
    fireEvent.change(patterns, { target: { value: 't/year | Tons per year' } });
    expect(button('permits.air.import.toPreview').disabled).toBe(true);

    fireEvent.click(button('permits.air.import.map.reanalyse'));
    await waitFor(() => expect(api.analyseAirImport).toHaveBeenCalledTimes(2));
    const options = api.analyseAirImport.mock.calls[1]![1];
    expect(options.spec?.fields?.[1]?.patterns).toEqual(['t/year', 'Tons per year']);
    expect(options.profileCode).toBeUndefined();
    await waitFor(() => expect(button('permits.air.import.toPreview').disabled).toBe(false));
  });

  it('дублікат імені поля блокує повторний аналіз', async () => {
    await toMapping();
    fireEvent.click(await screen.findByRole('button', { name: text('permits.air.import.map.edit') }));
    const names = await screen.findAllByLabelText(text('permits.air.import.map.fieldName'));
    fireEvent.change(names[1]!, { target: { value: 'naecode' } });
    expect(await screen.findByText(text('permits.air.import.map.nameDuplicate'))).toBeTruthy();
    expect(button('permits.air.import.map.reanalyse').disabled).toBe(true);
  });

  it('збереження як профілю без змін: одна форма, код з назви, специфікація профілю; профіль вибирається', async () => {
    api.saveImportProfile.mockResolvedValue({ ...profile(), code: 'MY_PROFILE', builtIn: false });
    await toMapping();
    // Exactly one way to save: the button outside «Advanced»; the form opens only from it.
    expect(screen.getAllByRole('button', { name: text('permits.air.import.map.saveProfile') })).toHaveLength(1);
    expect(screen.queryByLabelText(text('permits.air.import.save.name'))).toBeNull();
    fireEvent.click(await screen.findByRole('button', { name: text('permits.air.import.map.saveProfile') }));
    fireEvent.change(screen.getByLabelText(text('permits.air.import.save.name')), { target: { value: 'My profile' } });
    expect(screen.getByLabelText<HTMLInputElement>(text('permits.air.import.save.code')).value).toBe('MY_PROFILE');
    fireEvent.click(button('permits.air.import.save.submit'));
    await waitFor(() => expect(api.saveImportProfile).toHaveBeenCalledTimes(1));
    const [code, body] = api.saveImportProfile.mock.calls[0]!;
    expect(code).toBe('MY_PROFILE');
    expect(body.nameL10n['en']).toBe('My profile');
    expect(body.rowVersion).toBeNull();
    expect(body.spec).toEqual(profile().spec);
    expect(await screen.findByText('Profile MY_PROFILE saved and selected.')).toBeTruthy();
    expect(screen.queryByLabelText(text('permits.air.import.save.name'))).toBeNull();
    // Back in the File step the list is read anew and the saved profile is the chosen one.
    api.listImportProfiles.mockResolvedValue([
      ...profiles,
      { code: 'MY_PROFILE', builtIn: false, nameL10n: { en: 'My profile' }, updatedAt: '2026-11-07T00:00:00Z' },
    ]);
    fireEvent.click(button('wizard.back'));
    await waitFor(() => expect(api.listImportProfiles).toHaveBeenCalledTimes(2));
    await waitFor(() =>
      expect(screen.getByRole<HTMLInputElement>('textbox', { name: new RegExp(`^${text('permits.air.import.profile.label')}`) }).value).toBe(
        'My profile (MY_PROFILE)',
      ),
    );
  });

  it('одна форма збереження: профіль несе аркуш, рядки шапки, рядок даних і додане поле; далі імпорт іде цим профілем', async () => {
    const twoSheets = analysis({ sheetName: 'Table 3.3', sheetNames: ['Summary', 'Table 3.3'] });
    api.analyseAirImport.mockResolvedValue(twoSheets);
    api.saveImportProfile.mockResolvedValue({ ...profile(), code: 'SIMILAR_FILES', builtIn: false });
    await toMapping({ mayAddColumn: true });

    // 1. Where the table is: the other sheet and the header one row lower, then re-read.
    await pick(text('permits.air.import.rows.sheet'), /^Summary$/);
    fireEvent.change(screen.getByRole('textbox', { name: text('permits.air.import.rows.headerFrom') }), { target: { value: '5' } });
    fireEvent.click(button('permits.air.import.rows.apply'));
    await waitFor(() => expect(api.analyseAirImport).toHaveBeenCalledTimes(2));

    // 2. Column B of the file becomes a new column of the permit table and a field of the spec.
    fireEvent.click(screen.getByRole('button', { name: 'Add file column B as a new column' }));
    fireEvent.change(screen.getByRole('textbox', { name: text('permits.air.import.add.name') }), { target: { value: 'Workshop' } });
    fireEvent.click(button('permits.air.import.add.submit'));
    await waitFor(() => expect(api.analyseAirImport).toHaveBeenCalledTimes(3));

    // 3. Save as profile…: everything above is in the saved spec.
    fireEvent.click(button('permits.air.import.map.saveProfile'));
    fireEvent.change(screen.getByLabelText(text('permits.air.import.save.name')), { target: { value: 'Similar files' } });
    fireEvent.click(button('permits.air.import.save.submit'));
    await waitFor(() => expect(api.saveImportProfile).toHaveBeenCalledTimes(1));
    const [code, body] = api.saveImportProfile.mock.calls[0]!;
    expect(code).toBe('SIMILAR_FILES');
    expect(body.spec.sheet).toBe('Summary');
    expect(body.spec.header).toEqual({ fromRow: 5, toRow: 6 });
    expect(body.spec.dataStartRow).toBe(9);
    expect(body.spec.fields?.map((field) => field.name)).toEqual(['NaeCode', 'TonsPerYear', 'FILE_B']);
    expect(body.spec.fields?.[2]).toMatchObject({ column: 'B', level: 'Parent', parser: 'Text' });

    // 4. The wizard uses the saved profile: the preview goes by its code, without a spec of its own.
    await screen.findByText('Profile SIMILAR_FILES saved and selected.');
    await waitFor(() => expect(button('permits.air.import.toPreview').disabled).toBe(false));
    fireEvent.click(button('permits.air.import.toPreview'));
    await waitFor(() => expect(api.previewAirImport).toHaveBeenCalledTimes(1));
    expect(api.previewAirImport.mock.calls[0]![2]).toEqual({ profileCode: 'SIMILAR_FILES' });
  });

  it('вибір аркуша перечитує файл зі spec.sheet; рядки не чіпали — шапку того аркуша сервер знаходить сам', async () => {
    api.analyseAirImport.mockResolvedValue(analysis({ sheetName: 'Table 3.3', sheetNames: ['Summary', 'Table 3.3'] }));
    await toMapping();
    await pick(text('permits.air.import.rows.sheet'), /^Summary$/);
    fireEvent.click(button('permits.air.import.rows.apply'));
    await waitFor(() => expect(api.analyseAirImport).toHaveBeenCalledTimes(2));
    const options = api.analyseAirImport.mock.calls[1]![1];
    expect(options.spec?.sheet).toBe('Summary');
    expect(options.spec?.header).toBeUndefined();
    expect(options.spec?.dataStartRow).toBeUndefined();
    expect(options.spec?.fields).toEqual(profile().spec.fields);
  });

  it('один аркуш: вибору аркуша немає', async () => {
    await toMapping();
    expect(screen.queryByRole('textbox', { name: new RegExp(`^${text('permits.air.import.rows.sheet')}`) })).toBeNull();
  });

  it('«Matched columns» і знахідки — людськими назвами: вбудовані поля з каталогу, додане — назвою колонки типу, невідоме — як є', async () => {
    airType = airPermitType([['FILE_B', 'Workshop']]);
    api.analyseAirImport.mockResolvedValue(
      analysis({
        header: {
          headerFromRow: 4,
          headerToRow: 6,
          dataFromRow: 9,
          mapped: [
            { column: 7, columnLetter: 'G', field: 'naeCode', headerText: 'Source number', level: 'Parent' },
            { column: 9, columnLetter: 'I', field: 'hoursPerYear', headerText: 'Hours', level: 'Parent' },
            { column: 27, columnLetter: 'AA', field: 'tonsPerYear', headerText: 't/year', level: 'Child' },
            { column: 2, columnLetter: 'B', field: 'FILE_B', headerText: 'Shop', level: 'Parent' },
            { column: 3, columnLetter: 'C', field: 'Remark', headerText: 'Note', level: 'Child' },
          ],
          unmapped: [],
        },
        issues: [{ severity: 'Warning', messageKey: 'err.ECR-IMP-0422.regValueEmpty', row: 31, column: 'G', field: 'naeCode', args: { row: '31' } }],
      }),
    );
    await toMapping();
    const mapped = screen.getByRole('region', { name: text('permits.air.import.map.mappedAria') });
    const heads = within(mapped)
      .getAllByRole('rowheader')
      .map((cell) => cell.textContent);
    expect(heads.slice(0, 3)).toEqual([
      text('permits.air.import.field.naeCode'),
      text('permits.air.sources.hours'),
      text('permits.air.substances.ty'),
    ]);
    await waitFor(() => expect(within(mapped).getAllByRole('rowheader')[3]!.textContent).toBe('Workshop'));
    expect(within(mapped).getAllByRole('rowheader')[4]!.textContent).toBe('Remark');
    expect(mapped.textContent).not.toMatch(/naeCode|tonsPerYear|FILE_B/);
    expect(within(mapped).getAllByText(text('permits.air.import.map.level.parent')).length).toBe(3);
    const issues = screen.getByRole('region', { name: text('permits.air.import.issues.aria') });
    expect(within(issues).getByText(text('permits.air.import.field.naeCode'))).toBeTruthy();
  });

  it('без права редагувати профілі кнопки збереження немає', async () => {
    mount({ mayEditProfile: false });
    await fillFile();
    fireEvent.click(button('permits.air.import.analyse'));
    await screen.findByTestId('air-header-report');
    fireEvent.click(await screen.findByRole('button', { name: text('permits.air.import.map.edit') }));
    await screen.findAllByLabelText(text('permits.air.import.map.fieldName'));
    expect(screen.queryByRole('button', { name: text('permits.air.import.map.saveProfile') })).toBeNull();
  });

  it('аналіз із помилкою розбору не пускає далі', async () => {
    api.analyseAirImport.mockResolvedValue(analysis({ canContinue: false }));
    await toMapping();
    expect(button('permits.air.import.toPreview').disabled).toBe(true);
  });
});

describe('крок 3: перегляд, відсутні НАЕ, заміна чужих дозволів', () => {
  it('знахідки перекладені через каталог сервера (порожнє t/year - попередження з рядком і колонкою)', async () => {
    await toPreview();
    const table = screen.getByRole('region', { name: text('permits.air.import.issues.aria') });
    expect(within(table).getByText('31')).toBeTruthy();
    expect(within(table).getByText('AA')).toBeTruthy();
    expect(within(table).getByText(text('permits.air.import.issues.warning'))).toBeTruthy();
    expect(table.textContent).not.toContain('⟦');
  });

  it('без рішень про НАЕ й підтвердження застосувати не можна; причини названі', async () => {
    await toPreview();
    expect(button('permits.air.import.apply').disabled).toBe(true);
    const reasons = screen.getByRole('list', { name: text('permits.air.import.gate.aria') });
    expect(within(reasons).getByText(text('permits.air.import.gate.unresolved'))).toBeTruthy();
    expect(within(reasons).getByText(text('permits.air.import.gate.confirm'))).toBeTruthy();
  });

  it('чужі дозволи перелічені, підтвердження - явний прапорець', async () => {
    await toPreview();
    expect(screen.getByText('KZ46VCZ14622410: 12 NAE · 96 rows')).toBeTruthy();
    expect(screen.getByText('KZ79UKR00003155: 3 NAE · 24 rows')).toBeTruthy();
    const confirm = screen.getByRole('checkbox', { name: text('permits.air.import.replace.confirm') });
    expect((confirm as HTMLInputElement).checked).toBe(false);
  });

  it('рішення про НАЕ застарюють перегляд: потрібен новий, який шле рішення', async () => {
    await toPreview();
    fireEvent.click(screen.getByRole('button', { name: 'Skip all undecided (2)' }));
    expect(await screen.findByText(text('permits.air.import.stale.title'))).toBeTruthy();
    expect(button('permits.air.import.apply').disabled).toBe(true);

    api.previewAirImport.mockResolvedValue(preview({ fingerprint: 'fp-2' }));
    fireEvent.click(button('permits.air.import.rerun'));
    await waitFor(() => expect(api.previewAirImport).toHaveBeenCalledTimes(2));
    expect(api.previewAirImport.mock.calls[1]![2].resolutions).toEqual([
      { kind: 'nae', code: '3544', action: 'skip' },
      { kind: 'nae', code: '3545', action: 'skip' },
    ]);
    await waitFor(() => expect(screen.queryByText(text('permits.air.import.stale.title'))).toBeNull());
  });

  it('apply несе відбиток, рішення й власників; без підтвердження заміни чужих дозволів застосувати не можна', async () => {
    await toPreview();
    fireEvent.click(screen.getByRole('button', { name: 'Skip all undecided (2)' }));
    api.previewAirImport.mockResolvedValue(preview({ fingerprint: 'fp-2' }));
    fireEvent.click(await screen.findByRole('button', { name: text('permits.air.import.rerun') }));
    await waitFor(() => expect(screen.queryByText(text('permits.air.import.stale.title'))).toBeNull());

    expect(button('permits.air.import.apply').disabled).toBe(true);
    fireEvent.click(screen.getByRole('checkbox', { name: text('permits.air.import.replace.confirm') }));
    await waitFor(() => expect(button('permits.air.import.apply').disabled).toBe(false));

    fireEvent.click(button('permits.air.import.apply'));
    await waitFor(() => expect(api.applyAirImport).toHaveBeenCalledTimes(1));
    const [permitId, file, options, scope] = api.applyAirImport.mock.calls[0]!;
    expect(permitId).toBe(5);
    expect(file.name).toBe('limits.xlsx');
    expect(scope).toEqual({ regionEntryId: '11', areaEntryId: '21' });
    expect(options).toEqual({
      profileCode: 'ERA_TABLE_33',
      resolutions: [
        { kind: 'nae', code: '3544', action: 'skip' },
        { kind: 'nae', code: '3545', action: 'skip' },
      ],
      confirmedOtherOwners: ['7', '9'],
      expectedFingerprint: 'fp-2',
    });
  });

  it('створення під вузлом: вузол з дерева НАЕ їде як createUnder (id рядком)', async () => {
    await toPreview();
    const actions = screen.getAllByLabelText(/^Action for NAE/);
    fireEvent.click(actions[0]!);
    fireEvent.click(await screen.findByRole('option', { name: text('permits.air.import.missing.create') }));
    const under = await screen.findByLabelText('Node to create NAE 3544 under');
    fireEvent.click(under);
    fireEvent.click(await screen.findByRole('option', { name: 'Atyrau › Offshore › DC05_Island' }));
    fireEvent.click(screen.getByRole('button', { name: 'Skip all undecided (1)' }));

    api.previewAirImport.mockResolvedValue(preview({ fingerprint: 'fp-3' }));
    fireEvent.click(await screen.findByRole('button', { name: text('permits.air.import.rerun') }));
    await waitFor(() => expect(api.previewAirImport).toHaveBeenCalledTimes(2));
    expect(api.previewAirImport.mock.calls[1]![2].resolutions).toEqual([
      { kind: 'nae', code: '3544', action: 'create', createUnder: '3' },
      { kind: 'nae', code: '3545', action: 'skip' },
    ]);
  });

  it('помилки в плані блокують застосування, навіть коли все розв\'язано й підтверджено', async () => {
    api.previewAirImport.mockResolvedValue(
      preview({
        canApply: false,
        missing: [],
        issues: [{ severity: 'Error', messageKey: 'err.ECR-IMP-0422.regValueEmpty', row: 7, column: 'G', field: 'NaeCode', args: { row: '7' } }],
      }),
    );
    await toPreview();
    fireEvent.click(screen.getByRole('checkbox', { name: text('permits.air.import.replace.confirm') }));
    expect(button('permits.air.import.apply').disabled).toBe(true);
    expect(screen.getByText(text('permits.air.import.gate.errors'))).toBeTruthy();
  });

  it('лише для читання: «Apply» вимкнено навіть при зеленому плані', () => {
    const green = preview({
      missing: [],
      replace: { confirmationRequired: false, otherOwners: [], thisOwner: preview().preview.replace.thisOwner },
    });
    const noop = (): void => undefined;
    const props = {
      preview: green,
      draft: { ...EmptyDraft, file: xlsx(), region: 'ATYRAU' },
      onDraft: noop,
      stale: false,
      onRerun: noop,
      rerunning: false,
      onApply: noop,
      applying: false,
    };
    const client = new QueryClient();
    const view = render(
      <TestProviders>
        <QueryClientProvider client={client}>
          <PreviewStep {...props} mayApply />
        </QueryClientProvider>
      </TestProviders>,
    );
    expect(button('permits.air.import.apply').disabled).toBe(false);
    view.rerender(
      <TestProviders>
        <QueryClientProvider client={client}>
          <PreviewStep {...props} mayApply={false} />
        </QueryClientProvider>
      </TestProviders>,
    );
    expect(button('permits.air.import.apply').disabled).toBe(true);
  });

  it('відмова apply показується, застосований результат не з\'являється, потрібен новий перегляд', async () => {
    api.applyAirImport.mockRejectedValue(
      new EcrApiError({ title: 'Plan changed', status: 422, errorCode: 'ECR-IMP-0422', correlationId: 'c1' }),
    );
    api.previewAirImport.mockResolvedValue(
      preview({ missing: [], replace: { confirmationRequired: false, otherOwners: [], thisOwner: preview().preview.replace.thisOwner } }),
    );
    await toPreview();
    await waitFor(() => expect(button('permits.air.import.apply').disabled).toBe(false));
    fireEvent.click(button('permits.air.import.apply'));
    expect((await screen.findAllByRole('alert')).length).toBeGreaterThanOrEqual(2);
    expect(screen.queryByTestId('air-import-result')).toBeNull();
    await waitFor(() => expect(button('permits.air.import.apply').disabled).toBe(true));
    expect(screen.getByText(text('permits.air.import.stale.title'))).toBeTruthy();
  });
});

describe('крок 4: результат', () => {
  it('лічильники з відповіді apply, інші дозволи, посилання на картку', async () => {
    api.previewAirImport.mockResolvedValue(
      preview({ missing: [], replace: { confirmationRequired: false, otherOwners: [], thisOwner: preview().preview.replace.thisOwner } }),
    );
    await toPreview();
    await waitFor(() => expect(button('permits.air.import.apply').disabled).toBe(false));
    fireEvent.click(button('permits.air.import.apply'));
    const stats = await screen.findByTestId('air-import-result');
    expect(stats.textContent).toContain('373');
    expect(stats.textContent).toContain('2,624');
    expect(screen.getByText('KZ46VCZ14622410: 12 NAE · 96 rows')).toBeTruthy();
    expect(screen.getByRole('link', { name: text('permits.air.import.done.open') }).getAttribute('href')).toBe('/admin/permits/5?tab=air');
  });
});
