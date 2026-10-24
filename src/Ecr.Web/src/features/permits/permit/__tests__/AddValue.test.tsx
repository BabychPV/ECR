import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { apiFetch } from '@/api/client';
import { PermitPage } from '@/pages/admin/PermitPage';
import * as rowsApi from '@/features/registries/rows/api';
import { Catalog } from '@/test/__tests__/a11yFixtures';
import { TestProviders } from '@/test/render';
import * as permitsApi from '../../api/permitsApi';
import * as typesApi from '../../api/typesApi';
import * as listsApi from '../../lists/listsApi';
import type { PermitListDetail, PermitListDto } from '../../lists/listsApi';
import { addedItem } from '../AddValueDialog';
import { permit, permitType, registryRow, rowsFor } from './permitFixtures';

/**
 * «Not in the list? Add a value…» у формі рядка дозволу (PM-08e): кнопка лише для спільного списку й списку цього типу і
 * лише коли сервер каже `editable`; значення пишеться в сам список (`PUT /permit-lists/{key}/items`), пікер перечитується,
 * нове значення стає вибраним. Тексти - справжні, із `09-seed.sql`.
 *
 * Мутаційний доказ (вручну, відновлено): без `!list.editable` у `addValueFor` кнопка є й без права (падає «без права»);
 * `onAdded` без `changeField` лишає поле порожнім (падає «нове значення стає вибраним»); ключ `type:…` з іншим кодом
 * таблиці валить «список лише цього типу».
 */
vi.mock('@/shared/i18n', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/i18n')>()),
  t: (key: string, params?: Record<string, string | number>) =>
    (Catalog[key] ?? `⟦${key}⟧`).replace(/\{(\w+)\}/g, (match, name: string) =>
      params !== undefined && name in params ? String(params[name]) : match,
    ),
  language: () => 'en',
}));
vi.mock('@/api/client', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/api/client')>()),
  apiFetch: vi.fn(),
}));
vi.mock('@/features/permits/api/permitsApi', () => ({
  getPermit: vi.fn(),
  updatePermit: vi.fn(),
  activatePermit: vi.fn(),
  endPermit: vi.fn(),
  renewPermit: vi.fn(),
  deletePermit: vi.fn(),
}));
vi.mock('@/features/permits/api/typesApi', () => ({ getPermitType: vi.fn() }));
vi.mock('@/features/permits/lists/listsApi', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/features/permits/lists/listsApi')>()),
  listPermitLists: vi.fn(),
  changePermitListItems: vi.fn(),
}));
vi.mock('@/features/registries/rows/api', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/features/registries/rows/api')>()),
  getRegistryRows: vi.fn(),
  saveBatch: vi.fn(),
}));
vi.mock('@/shared/session/useSession', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/session/useSession')>()),
  useSession: () => ({ data: { permissions: ['Registry.View', 'Registry.EditData'], isSimulation: false } }),
}));

const listPermitLists = vi.mocked(listsApi.listPermitLists);
const changeItems = vi.mocked(listsApi.changePermitListItems);
const getRows = vi.mocked(rowsApi.getRegistryRows);

const SharedKey = 'shared:Substances';
const TypeKey = 'type:WaterDischarge:Limits:Use';

function listDto(key: string, scope: string, registryCode: string, editable: boolean): PermitListDto {
  return {
    key,
    scope,
    registryCode,
    typeCode: scope === 'SharedRegistry' ? null : 'WaterDischarge',
    nameL10n: { en: registryCode },
    itemCount: 2,
    editable,
    usedBy: [],
  };
}

function lists(over: { shared?: boolean; type?: boolean } = {}): PermitListDto[] {
  return [
    listDto(SharedKey, 'SharedRegistry', 'Substances', over.shared ?? true),
    listDto(TypeKey, 'TypeOnly', 'PRM_WD_Use', over.type ?? true),
  ];
}

/** Рядки реєстрів: після додавання значення в «Substances» з'являється `Toluene` (id 43). */
let added = false;

beforeEach(() => {
  added = false;
  vi.mocked(apiFetch).mockImplementation((path: string) =>
    Promise.resolve(path === '/api/v1/units' ? [{ id: 7, code: 't/yr' }] : []) as never,
  );
  vi.mocked(typesApi.getPermitType).mockResolvedValue(permitType());
  vi.mocked(permitsApi.getPermit).mockResolvedValue(permit());
  getRows.mockImplementation((code, query) => {
    const base = rowsFor(code, query?.parentEntryId);
    if (code === 'Substances' && added) {
      return Promise.resolve({ ...base, items: [...base.items, registryRow(43, {}, { display: 'Toluene' })] });
    }

    return Promise.resolve(base);
  });
  listPermitLists.mockResolvedValue(lists());
  changeItems.mockImplementation(() => {
    added = true;
    const detail: PermitListDetail = {
      list: lists()[0] as PermitListDto,
      items: [
        { id: 41, code: 'R41', displayL10n: { en: 'Methane' } },
        { id: 43, code: 'V003', displayL10n: { en: 'Toluene' } },
      ],
    };

    return Promise.resolve(detail);
  });
});
afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

async function openRow(over = permit()): Promise<HTMLElement> {
  vi.mocked(permitsApi.getPermit).mockResolvedValue(over);
  render(
    <TestProviders>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <MemoryRouter initialEntries={['/admin/permits/5?tab=Limits']}>
          <Routes>
            <Route path="/admin/permits/:id" element={<PermitPage />} />
          </Routes>
        </MemoryRouter>
      </QueryClientProvider>
    </TestProviders>,
  );
  fireEvent.click(await screen.findByText('Outfall A'));
  const dialog = await screen.findByRole('dialog', { name: 'Outfall A' });
  await within(dialog).findByRole('textbox', { name: /^Place/ });

  return dialog;
}

const addButtons = (dialog: HTMLElement): HTMLElement[] => [...dialog.querySelectorAll<HTMLElement>('[data-add-value]')];

describe('«Add a value…» у формі рядка', () => {
  it('є лише під спільним списком і списком цього типу, не під таблицею дозволу', async () => {
    const dialog = await openRow();

    await waitFor(() => expect(addButtons(dialog)).toHaveLength(2));
    expect([...dialog.querySelectorAll('[data-add-value-field]')].map((node) => node.getAttribute('data-add-value-field'))).toEqual(['Use', 'Substance']);
    expect(within(dialog).getAllByText('Not in the list? Add a value…')).toHaveLength(2);
    expect(listPermitLists).toHaveBeenCalledTimes(1);
  });

  it('без права на список кнопки немає: сервер каже editable=false', async () => {
    listPermitLists.mockResolvedValue(lists({ shared: false }));
    const dialog = await openRow();

    await waitFor(() => expect(addButtons(dialog)).toHaveLength(1));
    expect(dialog.querySelector('[data-add-value-field="Substance"]')).toBeNull();
    expect(dialog.querySelector('[data-add-value-field="Use"]')).not.toBeNull();
  });

  it('закінчений дозвіл: рядок лише для читання, перелік списків не читається, кнопок немає', async () => {
    const dialog = await openRow(permit({ status: 'Expired' }));

    expect(addButtons(dialog)).toHaveLength(0);
    expect(listPermitLists).not.toHaveBeenCalled();
  });

  it('спільний список: діалог з поясненням, PUT з Add, пікер перечитано, нове значення стає вибраним', async () => {
    const dialog = await openRow();
    await waitFor(() => expect(addButtons(dialog)).toHaveLength(2));
    const readsBefore = getRows.mock.calls.filter(([code]) => code === 'Substances').length;

    fireEvent.click(dialog.querySelector<HTMLElement>('[data-add-value-field="Substance"] [data-add-value]') as HTMLElement);
    const modal = await screen.findByRole('dialog', { name: 'Add a value' });
    expect(within(modal).getByText(/shared list Substances/)).toBeTruthy();

    fireEvent.change(within(modal).getByRole('textbox', { name: /^Value/ }), { target: { value: ' Toluene ' } });
    fireEvent.click(within(modal).getByRole('button', { name: 'Add value' }));

    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Add a value' })).toBeNull());
    expect(changeItems).toHaveBeenCalledWith(SharedKey, [{ action: 'Add', id: null, displayL10n: { en: 'Toluene' } }]);
    expect(getRows.mock.calls.filter(([code]) => code === 'Substances').length).toBeGreaterThan(readsBefore);
    expect(await within(dialog).findByRole('textbox', { name: /^Substance/ })).toHaveProperty('value', 'Toluene');
  });

  it('список лише цього типу: ключ type:…, пояснення про один тип', async () => {
    const dialog = await openRow();
    await waitFor(() => expect(addButtons(dialog)).toHaveLength(2));

    fireEvent.click(dialog.querySelector<HTMLElement>('[data-add-value-field="Use"] [data-add-value]') as HTMLElement);
    const modal = await screen.findByRole('dialog', { name: 'Add a value' });
    expect(within(modal).getByText('The value is added to the list of this permit type only.')).toBeTruthy();

    fireEvent.change(within(modal).getByRole('textbox', { name: /^Value/ }), { target: { value: 'Cleaning' } });
    fireEvent.click(within(modal).getByRole('button', { name: 'Add value' }));

    await waitFor(() => expect(changeItems).toHaveBeenCalledTimes(1));
    expect(changeItems).toHaveBeenCalledWith(TypeKey, [{ action: 'Add', id: null, displayL10n: { en: 'Cleaning' } }]);
  });

  it('відмова сервера: діалог лишається, поле форми не змінюється', async () => {
    changeItems.mockRejectedValue(new Error('forbidden'));
    const dialog = await openRow();
    await waitFor(() => expect(addButtons(dialog)).toHaveLength(2));
    const before = (within(dialog).getByRole('textbox', { name: /^Substance/ }) as HTMLInputElement).value;

    fireEvent.click(dialog.querySelector<HTMLElement>('[data-add-value-field="Substance"] [data-add-value]') as HTMLElement);
    const modal = await screen.findByRole('dialog', { name: 'Add a value' });
    fireEvent.change(within(modal).getByRole('textbox', { name: /^Value/ }), { target: { value: 'Toluene' } });
    fireEvent.click(within(modal).getByRole('button', { name: 'Add value' }));

    await waitFor(() => expect(changeItems).toHaveBeenCalledTimes(1));
    expect(screen.getByRole('dialog', { name: 'Add a value' })).toBeTruthy();
    expect((within(dialog).getByRole('textbox', { name: /^Substance/ }) as HTMLInputElement).value).toBe(before);
  });

  it('Cancel закриває діалог без запиту, порожнє значення не відправляється', async () => {
    const dialog = await openRow();
    await waitFor(() => expect(addButtons(dialog)).toHaveLength(2));

    fireEvent.click(addButtons(dialog)[0] as HTMLElement);
    const modal = await screen.findByRole('dialog', { name: 'Add a value' });
    expect(within(modal).getByRole('button', { name: 'Add value' })).toHaveProperty('disabled', true);
    fireEvent.click(within(modal).getByRole('button', { name: 'Cancel' }));

    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Add a value' })).toBeNull());
    expect(changeItems).not.toHaveBeenCalled();
  });
});

describe('addedItem', () => {
  const items = [
    { id: 1, code: 'V1', displayL10n: { en: 'Methane' } },
    { id: 7, code: 'V7', displayL10n: { en: 'toluene' } },
    { id: 9, code: 'V9', displayL10n: { en: 'Toluene' } },
  ];

  it('найновіший запис із введеним підписом, без урахування регістру й пробілів', () => {
    expect(addedItem(items, ' TOLUENE ')?.id).toBe(9);
  });

  it('підпис не знайдено - останній запис списку; порожній список - нічого', () => {
    expect(addedItem(items, 'Other')?.id).toBe(9);
    expect(addedItem([], 'Other')).toBeUndefined();
  });
});
