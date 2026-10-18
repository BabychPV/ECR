import type { JSX } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom';
import { apiFetch, EcrApiError } from '@/api/client';
import { PermitTypeEditorPage } from '@/pages/admin/PermitTypeEditorPage';
import { Catalog } from '@/test/__tests__/a11yFixtures';
import { TestProviders } from '@/test/render';
import * as typesApi from '../../api/typesApi';
import { AutosaveDelayMs } from '../useTypeEditor';
import { permitType } from './fixtures';

/**
 * Сторінка конструктора типу (PM-08b): шапка, вкладки, структура, кнопки діалогів, стани. Тексти —
 * справжні, із `09-seed.sql`.
 *
 * Мутаційний доказ (виконано вручну, відновлено): у `TypeEditorHeader` `editable ?` → `!editable ?` для
 * «Publish…»/«New version…» валить 3 тести; у `StructureTab` вибір таблиці без перевірки її існування
 * валить «невідома таблиця в адресі»; `closePanel` без дії валить «Close закриває».
 */
vi.mock('@/shared/i18n', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/i18n')>()),
  t: (key: string, params?: Record<string, string | number>) =>
    (Catalog[key] ?? `⟦${key}⟧`).replace(/\{(\w+)\}/g, (match, name: string) =>
      params !== undefined && name in params ? String(params[name]) : match,
    ),
}));
vi.mock('@/api/client', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/api/client')>()),
  apiFetch: vi.fn(),
}));
vi.mock('../../api/typesApi', () => ({ getPermitType: vi.fn(), replacePermitType: vi.fn() }));

const get = vi.mocked(typesApi.getPermitType);
const put = vi.mocked(typesApi.replacePermitType);
const fetchMock = vi.mocked(apiFetch);

beforeEach(() => {
  fetchMock.mockImplementation((path: string) =>
    Promise.resolve(path === '/api/v1/units' ? [{ id: 7, code: 't/yr' }] : []) as never,
  );
});
afterEach(() => {
  vi.useRealTimers();
  cleanup();
  vi.clearAllMocks();
});

function Probe(): JSX.Element {
  const location = useLocation();
  return <output data-testid="url">{location.pathname + location.search}</output>;
}

async function open(url = '/admin/permits/types/WaterDischarge', type = permitType()) {
  get.mockResolvedValue(type);
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <TestProviders>
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={[url]}>
          <Routes>
            <Route
              path="/admin/permits/types/:code"
              element={
                <>
                  <PermitTypeEditorPage />
                  <Probe />
                </>
              }
            />
          </Routes>
        </MemoryRouter>
      </QueryClientProvider>
    </TestProviders>,
  );
  await screen.findByRole('tablist');
}

const params = (): URLSearchParams => new URLSearchParams((screen.getByTestId('url').textContent ?? '').split('?')[1] ?? '');

describe('шапка', () => {
  it('назва, друга мова, бейдж чернетки, рядок збереження, дії', async () => {
    await open();
    expect(screen.getByRole('heading', { level: 1 }).textContent).toBe('Water discharge');
    expect(screen.getByText('Сброс воды')).toBeTruthy();
    expect(screen.getByText('Draft v1')).toBeTruthy();
    expect(screen.getByText(/^Saved \d\d:\d\d$/)).toBeTruthy();
    expect(screen.getByRole('link', { name: '← Back to Permit types' }).getAttribute('href')).toBe('/admin/permits/types');
    expect(screen.getByRole('button', { name: 'Check' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Publish…' })).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'New version…' })).toBeNull();
  });

  it('вкладки: Structure, Checks із лічильником, Versions', async () => {
    await open();
    const tabs = within(screen.getByRole('tablist', { name: 'Permit type sections' })).getAllByRole('tab');
    expect(tabs.map((tab) => tab.textContent)).toEqual(['Structure', 'Checks0', 'Versions']);
    expect(tabs[0]!.getAttribute('aria-selected')).toBe('true');
  });

  it('ніколи не опублікований тип: меню «…» із «Delete type…»; не показується, коли тип уже публікувався', async () => {
    await open();
    const user = userEvent.setup();
    await user.click(screen.getByRole('button', { name: 'More actions' }));
    await user.click(await screen.findByRole('menuitem', { name: 'Delete type…' }));
    expect(params().get('dialog')).toBe('delete-type');
    cleanup();
    await open('/admin/permits/types/WaterDischarge', permitType({ version: 2, draftVersion: 3 }));
    expect(screen.queryByRole('button', { name: 'More actions' })).toBeNull();
    expect(screen.getByText('Draft v3')).toBeTruthy();
  });
});

describe('вкладки та адреса', () => {
  it('Checks → ?tab=rules зі слотом PM-08c; Structure прибирає параметр', async () => {
    await open();
    const user = userEvent.setup();
    await user.click(screen.getByRole('tab', { name: /Checks/ }));
    expect(params().get('tab')).toBe('rules');
    expect(await screen.findByText('Checks cannot be shown yet')).toBeTruthy();
    await user.click(screen.getByRole('tab', { name: 'Versions' }));
    expect(params().get('tab')).toBe('versions');
    expect(await screen.findByText('Versions cannot be shown yet')).toBeTruthy();
    await user.click(screen.getByRole('tab', { name: 'Structure' }));
    expect(params().has('tab')).toBe(false);
  });

  it('?tab=versions відкриває потрібну вкладку; невідомий tab — Structure', async () => {
    await open('/admin/permits/types/WaterDischarge?tab=versions');
    expect(screen.getByRole('tab', { name: 'Versions' }).getAttribute('aria-selected')).toBe('true');
    cleanup();
    await open('/admin/permits/types/WaterDischarge?tab=nonsense');
    expect(screen.getByRole('tab', { name: 'Structure' }).getAttribute('aria-selected')).toBe('true');
  });
});

describe('структура', () => {
  it('перша таблиця вибрана: заголовок, кількість стовпців, підказка ключа, Under the hood', async () => {
    await open();
    expect(screen.getByRole('heading', { level: 2, name: /^Discharge/ }).textContent).toBe('Discharge3 columns');
    expect(screen.getByText('Unique rows: Permit + Water_body + Sub_period')).toBeTruthy();
    expect(screen.getByText('Under the hood')).toBeTruthy();
    expect(screen.getAllByText('PRM_WaterDischarge_Discharge').length).toBeGreaterThanOrEqual(2);
    expect(screen.getByRole('row', { name: 'Annual' }).textContent).toContain('t/yr');
  });

  it('вибір таблиці у дереві міняє заголовок і адресу, знімає інспектор', async () => {
    await open('/admin/permits/types/WaterDischarge?panel=Annual');
    expect(await screen.findByTestId('column-inspector')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: /^Storage/ }));
    expect(params().get('t')).toBe('Storage');
    expect(params().has('panel')).toBe(false);
    expect(screen.getByRole('heading', { level: 2, name: /^Storage/ })).toBeTruthy();
    expect(screen.queryByTestId('column-inspector')).toBeNull();
  });

  it('невідома таблиця в адресі — перша', async () => {
    await open('/admin/permits/types/WaterDischarge?t=Nope');
    expect(screen.getByRole('heading', { level: 2, name: /^Discharge/ })).toBeTruthy();
  });

  it('«Permit details»: поля шапки лише для читання, без «Add column»', async () => {
    await open();
    fireEvent.click(screen.getByRole('button', { name: /^Permit details/ }));
    expect(screen.getByRole('heading', { level: 2, name: /^Permit details/ })).toBeTruthy();
    expect(screen.getByText(/fixed by the system/)).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Add column' })).toBeNull();
    const rows = screen.getAllByRole('row').slice(1);
    expect(rows.map((r) => r.textContent?.includes('Permit_Number'))).toContain(true);
    fireEvent.click(rows[0]!);
    expect(params().has('panel')).toBe(false);
  });

  it('клік по стовпцю відкриває інспектор (?panel=), «Close» закриває', async () => {
    await open();
    fireEvent.click(screen.getByRole('row', { name: 'Annual' }));
    expect(params().get('panel')).toBe('Annual');
    const inspector = await screen.findByTestId('column-inspector');
    expect(within(inspector).getByRole('heading', { name: 'Annual' })).toBeTruthy();
    fireEvent.click(within(inspector).getByRole('button', { name: 'Close column properties' }));
    expect(params().has('panel')).toBe(false);
    await waitFor(() => expect(screen.queryByTestId('column-inspector')).toBeNull());
  });

  it('правка в інспекторі: стовпець у таблиці змінюється, шапка каже «1 unsaved changes»', async () => {
    await open('/admin/permits/types/WaterDischarge?panel=Annual');
    const inspector = await screen.findByTestId('column-inspector');
    const user = userEvent.setup();
    await user.clear(within(inspector).getByLabelText(/Name \(English\)/));
    await user.type(within(inspector).getByLabelText(/Name \(English\)/), 'Yearly');
    expect(screen.getByRole('row', { name: 'Yearly' })).toBeTruthy();
    expect(screen.getByText('1 unsaved changes')).toBeTruthy();
  });

  it('позначили стовпець ключем — підказка ключа й «обовʼязковий» оновились', async () => {
    await open('/admin/permits/types/WaterDischarge?panel=Annual');
    const inspector = await screen.findByTestId('column-inspector');
    await userEvent.setup().click(within(inspector).getByRole('switch', { name: /unique key/ }));
    expect(screen.getByText('Unique rows: Permit + Water_body + Sub_period + Annual')).toBeTruthy();
    expect((within(inspector).getByRole('switch', { name: /^Required/ }) as HTMLInputElement).disabled).toBe(true);
    expect(within(screen.getByRole('row', { name: 'Annual' })).getAllByRole('img').map((i) => i.getAttribute('aria-label'))).toEqual([
      'Required',
      'Part of key',
    ]);
  });

  it('вузький екран: кнопка «Structure» відкриває дерево (data-open)', async () => {
    await open();
    const tree = screen.getByRole('complementary', { name: 'Tables of this type', hidden: true });
    expect(tree.getAttribute('data-open')).toBe('false');
    const toggle = screen.getByRole('button', { name: 'Structure', hidden: true });
    fireEvent.click(toggle);
    expect(tree.getAttribute('data-open')).toBe('true');
    expect(toggle.getAttribute('aria-expanded')).toBe('true');
  });
});

describe('кнопки діалогів ставлять ?dialog=', () => {
  it.each([
    ['Add table', 'add-table'],
    ['Add another table', 'add-table'],
    ['Add table…', 'add-table'],
    ['Add column', 'add-column'],
    ['Publish…', 'publish-type'],
  ])('«%s» → %s', async (name, dialog) => {
    await open();
    fireEvent.click(screen.getByRole('button', { name }));
    expect(params().get('dialog')).toBe(dialog);
  });

  it('More → «Add months…» і «Delete table…» (з кодом таблиці)', async () => {
    await open();
    const user = userEvent.setup();
    await user.click(screen.getByRole('button', { name: 'More' }));
    await user.click(await screen.findByRole('menuitem', { name: 'Add months January–December…' }));
    expect(params().get('dialog')).toBe('add-series');
    await user.click(screen.getByRole('button', { name: 'More' }));
    await user.click(await screen.findByRole('menuitem', { name: 'Delete table…' }));
    expect(params().get('dialog')).toBe('delete-table');
    expect(params().get('table')).toBe('Discharge');
    expect(params().get('t') ?? 'Discharge').toBe('Discharge');
  });
});

describe('опублікований тип без чернетки', () => {
  const published = permitType({ status: 'Published', hasUnpublishedChanges: false, draftVersion: null, version: 2 });

  it('читання: бейдж, банер, «New version…», жодних кнопок правки', async () => {
    await open('/admin/permits/types/WaterDischarge', published);
    expect(screen.getByText('Published v2')).toBeTruthy();
    expect(screen.getByText('This version is published')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Publish…' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Check' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Add table' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Add column' })).toBeNull();
    fireEvent.click(screen.getAllByRole('button', { name: 'New version…' })[0]!);
    expect(params().get('dialog')).toBe('new-version');
  });

  it('інспектор відкривається, але поля вимкнені', async () => {
    await open('/admin/permits/types/WaterDischarge?panel=Annual', published);
    const inspector = await screen.findByTestId('column-inspector');
    expect((within(inspector).getByLabelText(/Name \(English\)/) as HTMLInputElement).disabled).toBe(true);
  });
});

describe('Check і вади копії', () => {
  it('чиста копія: «No problems found»; банер закривається', async () => {
    await open();
    fireEvent.click(screen.getByRole('button', { name: 'Check' }));
    expect(screen.getByText(/No problems found/)).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Close the result' }));
    expect(screen.queryByText(/No problems found/)).toBeNull();
  });

  it('таблиця без ключа: смуга вад над структурою, Check перелічує те саме, інспектор — у стовпця', async () => {
    await open('/admin/permits/types/WaterDischarge?t=Storage&panel=Place');
    const inspector = await screen.findByTestId('column-inspector');
    await userEvent.setup().click(within(inspector).getByRole('switch', { name: /unique key/ }));
    expect(screen.getByText('1 to fix before this can be saved')).toBeTruthy();
    expect(screen.getAllByText(/has no key column/).length).toBeGreaterThanOrEqual(1);
    expect(screen.getByText('Unique rows: Permit (no key column yet)')).toBeTruthy();
    expect(screen.getByText(/1 unsaved changes · 1 to fix before saving/)).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Check' }));
    expect(screen.getAllByText('1 to fix before this can be saved')).toHaveLength(2);
  });
});

describe('помилки', () => {
  it('тип не знайдено: код помилки, «назад» і порожня шапка', async () => {
    get.mockRejectedValue(new EcrApiError({ title: 'Not found', status: 404, errorCode: 'ECR-PRM-0404', correlationId: 'c-9' }));
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <TestProviders>
        <QueryClientProvider client={client}>
          <MemoryRouter initialEntries={['/admin/permits/types/Nope']}>
            <Routes>
              <Route path="/admin/permits/types/:code" element={<PermitTypeEditorPage />} />
            </Routes>
          </MemoryRouter>
        </QueryClientProvider>
      </TestProviders>,
    );
    expect(await screen.findByText('ECR-PRM-0404')).toBeTruthy();
    expect(screen.getByRole('heading', { level: 1 }).textContent).toBe('Nope');
    expect(screen.getByRole('link', { name: '← Back to Permit types' })).toBeTruthy();
  });

  it('конфлікт при збереженні: текст помилки сервера, «Reload» повертає збережене', async () => {
    await open('/admin/permits/types/WaterDischarge?panel=Annual');
    const inspector = await screen.findByTestId('column-inspector');
    vi.useFakeTimers();
    put.mockRejectedValue(
      new EcrApiError({ title: 'Changed', status: 409, errorCode: 'ECR-PRM-4093', correlationId: 'c-5', extensions2: { messageKey: 'err.ECR-PRM-4093.changed' } }),
    );
    fireEvent.change(within(inspector).getByLabelText(/Name \(English\)/), { target: { value: 'Mine' } });
    await act(async () => {
      await vi.advanceTimersByTimeAsync(AutosaveDelayMs);
    });
    vi.useRealTimers();
    expect(put).toHaveBeenCalledTimes(1);
    expect((await screen.findAllByText(/Changed by someone else/)).length).toBeGreaterThanOrEqual(1);
    expect(screen.getByText('ECR-PRM-4093')).toBeTruthy();

    get.mockResolvedValue(permitType({ rowVersion: 'rv-9' }));
    const reload = screen.getAllByRole('button', { name: 'Reload' })[0]!;
    await act(async () => {
      fireEvent.click(reload);
    });
    await waitFor(() => expect(screen.queryAllByText(/Changed by someone else/)).toHaveLength(0));
    expect(screen.getByRole('row', { name: 'Annual' })).toBeTruthy();
  });
});
