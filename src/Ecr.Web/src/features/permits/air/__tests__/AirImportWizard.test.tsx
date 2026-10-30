import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { EcrApiError } from '@/api/client';
import { Catalog } from '@/test/__tests__/a11yFixtures';
import { TestProviders } from '@/test/render';
import * as airApi from '@/features/permits/air/airApi';
import { AirImportWizard } from '../import/AirImportWizard';
import { EmptyDraft } from '../import/importModel';
import { PreviewStep } from '../import/PreviewStep';
import { analysis, preview, profile, profiles, result, tree, xlsx } from './airFixtures';

/**
 * Майстер імпорту лімітів із Excel (NAE-план N5): файл -> відповідність -> перегляд -> застосування. API замоканий;
 * тексти - справжні з `09-seed.sql`.
 *
 * Мутаційні докази (виконано вручну, кожну мутацію відновлено):
 * - `applyGate`: прибрано `confirm` валить «без підтвердження заміни чужих дозволів застосувати не можна» (тут і в importModel);
 * - `buildOptions`: `expectedFingerprint` не передається валить «apply несе відбиток і власників»;
 * - `previewSignature`: рішення виключені з підпису валить «рішення про НАЕ застарюють перегляд»;
 * - `AirImportWizard.runApply`: прибрано `setForceStale(true)` валить «відмова apply вимагає нового перегляду».
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
}));

const api = vi.mocked(airApi);
const text = (key: string): string => Catalog[key] ?? key;

beforeEach(() => {
  api.listImportProfiles.mockResolvedValue(profiles);
  api.getImportProfile.mockResolvedValue(profile());
  api.analyseAirImport.mockResolvedValue(analysis());
  api.previewAirImport.mockResolvedValue(preview());
  api.applyAirImport.mockResolvedValue(result());
  api.getNaeTree.mockResolvedValue(tree());
});
afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

function mount(props: { mayImport?: boolean; mayEditProfile?: boolean } = {}): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <TestProviders>
      <QueryClientProvider client={client}>
        <MemoryRouter>
          <AirImportWizard permitId={5} mayImport={props.mayImport ?? true} mayEditProfile={props.mayEditProfile ?? true} />
        </MemoryRouter>
      </QueryClientProvider>
    </TestProviders>,
  );
}

const button = (key: string): HTMLButtonElement => screen.getByRole('button', { name: text(key) });

async function fillFile(region = 'atyrau', area = 'offshore'): Promise<void> {
  await screen.findByLabelText(new RegExp(text('permits.air.import.region.label')));
  const input = document.querySelector<HTMLInputElement>('input[type="file"]');
  if (input === null) throw new Error('file input');
  await userEvent.upload(input, xlsx());
  fireEvent.change(screen.getByLabelText(new RegExp(text('permits.air.import.region.label'))), { target: { value: region } });
  fireEvent.change(screen.getByLabelText(new RegExp(text('permits.air.import.area.label'))), { target: { value: area } });
}

async function toMapping(): Promise<void> {
  mount();
  await fillFile();
  fireEvent.click(button('permits.air.import.analyse'));
  await screen.findByTestId('air-header-report');
}

async function toPreview(): Promise<void> {
  await toMapping();
  fireEvent.click(button('permits.air.import.toPreview'));
  await screen.findByText(text('permits.air.import.replace.title'));
}

describe('крок 1: файл', () => {
  it('без файлу й регіону далі не йде й нічого не шле', async () => {
    mount();
    await screen.findByLabelText(new RegExp(text('permits.air.import.region.label')));
    fireEvent.click(button('permits.air.import.analyse'));
    expect(await screen.findByText(text('permits.air.import.file.required'))).toBeTruthy();
    expect(api.analyseAirImport).not.toHaveBeenCalled();
  });

  it('аналіз шле файл і options: профіль за замовчуванням, регіон великими, ділянка', async () => {
    await toMapping();
    expect(api.analyseAirImport).toHaveBeenCalledTimes(1);
    const [file, options] = api.analyseAirImport.mock.calls[0]!;
    expect(file.name).toBe('limits.xlsx');
    expect(options).toEqual({ profileCode: 'ERA_TABLE_33', region: 'ATYRAU', area: 'OFFSHORE' });
  });

  it('лише для читання: «Next» вимкнено', async () => {
    mount({ mayImport: false });
    await screen.findByLabelText(new RegExp(text('permits.air.import.region.label')));
    expect(button('permits.air.import.analyse').disabled).toBe(true);
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

  it('збереження як профілю: PUT із кодом, назвою й специфікацією; профіль вибирається', async () => {
    api.saveImportProfile.mockResolvedValue({ ...profile(), code: 'MY_PROFILE', builtIn: false });
    await toMapping();
    fireEvent.click(await screen.findByRole('button', { name: text('permits.air.import.map.edit') }));
    fireEvent.click(await screen.findByRole('button', { name: text('permits.air.import.map.saveProfile') }));
    fireEvent.change(screen.getByLabelText(text('permits.air.import.save.code')), { target: { value: 'my_profile' } });
    fireEvent.change(screen.getByLabelText(text('permits.air.import.save.name')), { target: { value: 'Mine' } });
    fireEvent.click(button('permits.air.import.save.submit'));
    await waitFor(() => expect(api.saveImportProfile).toHaveBeenCalledTimes(1));
    const [code, body] = api.saveImportProfile.mock.calls[0]!;
    expect(code).toBe('MY_PROFILE');
    expect(body.nameL10n['en']).toBe('Mine');
    expect(body.rowVersion).toBeNull();
    expect(body.spec.fields).toHaveLength(2);
    expect(await screen.findByText('Profile MY_PROFILE saved and selected.')).toBeTruthy();
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
    const [permitId, file, options] = api.applyAirImport.mock.calls[0]!;
    expect(permitId).toBe(5);
    expect(file.name).toBe('limits.xlsx');
    expect(options).toEqual({
      profileCode: 'ERA_TABLE_33',
      region: 'ATYRAU',
      area: 'OFFSHORE',
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
