import type { JSX } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom';
import { apiFetch, EcrApiError } from '@/api/client';
import { PermitPage } from '@/pages/admin/PermitPage';
import * as rowsApi from '@/features/registries/rows/api';
import type { RegistryBatchResult } from '@/features/registries/rows/api';
import { Catalog } from '@/test/__tests__/a11yFixtures';
import { TestProviders } from '@/test/render';
import * as permitsApi from '../../api/permitsApi';
import * as typesApi from '../../api/typesApi';
import { LimitsRegistry, page, permit, permitType, rowsFor } from './permitFixtures';

/**
 * Екран дозволу (PM-07): шапка-форма, вкладки, життєвий цикл за станом і правами, форма рядка, вставка з Excel.
 * Тексти — справжні, із `09-seed.sql`.
 *
 * Мутаційні докази (виконано вручну запуском, кожну мутацію відновлено):
 * - `capabilities`: `editRows: mayEdit && !expired` → `editRows: mayEdit` валить 3 тести (rules, «закінчений: рядок
 *   лише читання», порожня таблиця закінченого);
 * - `updateRowItem`: `baseVersion: null` валить «наявний рядок» тут і в `model.test.ts`; без `PERMIT` у `newRowItem`
 *   валиться `model.test.ts`;
 * - `HeaderForm`: `rowVersion: ''` валить «збереження шле всю шапку з токеном»;
 * - `planImport`: `clean: true` валить 3 тести (рядок із нерозпізнаною клітинкою не має йти на запис);
 * - `PermitPage`: умова `{true && <Button Activate…` валить «чинний: Renew… … Activate… немає»;
 * - `RowDrawer`: `validateRow(…, undefined)` (рядок конфліктує сам із собою) валить 2 тести;
 * - `runImport`: без зупинки на відхиленому шматку валить «справжній запис зупиняється…» (`importRows.test.ts`).
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
vi.mock('../../api/permitsApi', () => ({
  getPermit: vi.fn(),
  updatePermit: vi.fn(),
  activatePermit: vi.fn(),
  endPermit: vi.fn(),
  renewPermit: vi.fn(),
  deletePermit: vi.fn(),
}));
vi.mock('../../api/typesApi', () => ({ getPermitType: vi.fn() }));
vi.mock('@/features/registries/rows/api', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/features/registries/rows/api')>()),
  getRegistryRows: vi.fn(),
  saveBatch: vi.fn(),
}));
const session = vi.hoisted(() => ({ permissions: [] as string[] }));
vi.mock('@/shared/session/useSession', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/session/useSession')>()),
  useSession: () => ({ data: { permissions: session.permissions, isSimulation: false } }),
}));

const getPermit = vi.mocked(permitsApi.getPermit);
const updatePermit = vi.mocked(permitsApi.updatePermit);
const activatePermit = vi.mocked(permitsApi.activatePermit);
const endPermit = vi.mocked(permitsApi.endPermit);
const renewPermit = vi.mocked(permitsApi.renewPermit);
const deletePermit = vi.mocked(permitsApi.deletePermit);
const saveBatch = vi.mocked(rowsApi.saveBatch);
const getRows = vi.mocked(rowsApi.getRegistryRows);

const Editor = ['Registry.View', 'Registry.EditData'];
const Viewer = ['Registry.View'];

function applied(clientRowId = 'save'): RegistryBatchResult {
  return {
    added: 1,
    applied: true,
    deleted: 0,
    dryRun: false,
    rows: [{ clientRowId, entryId: 99, errors: [], status: 'added', version: 'v99' }],
    unchanged: 0,
    updated: 0,
  };
}

beforeEach(() => {
  session.permissions = Editor;
  vi.mocked(apiFetch).mockImplementation((path: string) =>
    Promise.resolve(path === '/api/v1/units' ? [{ id: 7, code: 't/yr' }] : []) as never,
  );
  vi.mocked(typesApi.getPermitType).mockResolvedValue(permitType());
  // Як сервер: реєстри темпоральні, читання без `asOf` — відмова (`422 ECR-REQ-0422`, e2e-стенд 19.10).
  getRows.mockImplementation((code, query) =>
    query?.asOf === undefined
      ? Promise.reject(new Error('ECR-REQ-0422: asOf is required'))
      : Promise.resolve(rowsFor(code, query.parentEntryId)),
  );
  saveBatch.mockResolvedValue(applied());
});
afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

function Probe(): JSX.Element {
  const location = useLocation();
  return <output data-testid="url">{location.pathname + location.search}</output>;
}

async function open(url = '/admin/permits/5', over = permit()): Promise<void> {
  getPermit.mockResolvedValue(over);
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <TestProviders>
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={[url]}>
          <Routes>
            <Route
              path="/admin/permits/:id"
              element={
                <>
                  <PermitPage />
                  <Probe />
                </>
              }
            />
            <Route path="/admin/permits" element={<Probe />} />
          </Routes>
        </MemoryRouter>
      </QueryClientProvider>
    </TestProviders>,
  );
  await screen.findByRole('heading', { level: 1, name: over.number });
}

const url = (): string => screen.getByTestId('url').textContent ?? '';

async function openLimits(over = permit()): Promise<void> {
  await open('/admin/permits/5?tab=Limits', over);
  await screen.findByText('Outfall A');
}

describe('шапка-форма', () => {
  it('чернетка з правом запису: групи полів зі значеннями', async () => {
    await open();
    const form = screen.getByRole('form', { name: 'Permit details' });
    expect(within(form).getByRole('textbox', { name: /^Number/ })).toHaveProperty('value', 'KZ-100');
    expect(within(form).getByRole('textbox', { name: /^Location/ })).toHaveProperty('value', 'Central facility');
    for (const legend of ['Identity', 'Dates and validity', 'Reporting forms']) {
      expect(within(form).getByText(legend)).toBeTruthy();
    }
    expect(within(form).getByRole('checkbox', { name: 'Form 300' })).toHaveProperty('checked', true);
    expect(within(form).getByRole('checkbox', { name: 'TOx' })).toHaveProperty('checked', false);
    expect(within(form).getByRole('button', { name: 'Save changes' })).toHaveProperty('disabled', true);
  });

  it('збереження шле всю шапку з токеном rowVersion і перезбирає форму з відповіді', async () => {
    updatePermit.mockResolvedValue(permit({ number: 'KZ-101', rowVersion: 'permit-rv-2' }));
    await open();
    const number = screen.getByRole('textbox', { name: /^Number/ });
    fireEvent.change(number, { target: { value: ' KZ-101 ' } });
    fireEvent.click(screen.getByRole('checkbox', { name: 'TOx' }));
    fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));

    await waitFor(() => expect(updatePermit).toHaveBeenCalledTimes(1));
    expect(updatePermit).toHaveBeenCalledWith(5, {
      number: 'KZ-101',
      location: 'Central facility',
      issueDate: '2026-01-05',
      validFrom: '2026-02-01',
      validTo: '2027-02-01',
      actualEnd: null,
      reportingFlags: { form300: true, form301: false, form302: false, tox: true },
      rowVersion: 'permit-rv-1',
    });
    await waitFor(() => expect(screen.getByRole('textbox', { name: /^Number/ })).toHaveProperty('value', 'KZ-101'));
  });

  it('порожній номер не відправляється: помилка біля поля', async () => {
    await open();
    fireEvent.change(screen.getByRole('textbox', { name: /^Number/ }), { target: { value: '  ' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));
    expect(await screen.findByText('Enter the permit number.')).toBeTruthy();
    expect(updatePermit).not.toHaveBeenCalled();
  });

  it('Discard повертає значення з сервера', async () => {
    await open();
    const number = screen.getByRole('textbox', { name: /^Number/ });
    fireEvent.change(number, { target: { value: 'ZZ' } });
    fireEvent.click(screen.getByRole('button', { name: 'Discard changes' }));
    expect(screen.getByRole('textbox', { name: /^Number/ })).toHaveProperty('value', 'KZ-100');
  });

  it('закінчений дозвіл і глядач бачать перелік пар, а не форму', async () => {
    await open('/admin/permits/5', permit({ status: 'Expired', actualEnd: '2026-09-01' }));
    expect(screen.queryByRole('form')).toBeNull();
    expect(screen.queryByRole('button', { name: 'Save changes' })).toBeNull();
    expect(screen.getByText('KZ-100', { selector: 'dd' })).toBeTruthy();
    expect(screen.getByText('This permit has expired and is read-only here')).toBeTruthy();
    cleanup();

    session.permissions = Viewer;
    await open('/admin/permits/5', permit({ status: 'Active' }));
    expect(screen.queryByRole('form')).toBeNull();
  });
});

describe('вкладки', () => {
  it('Overview, по вкладці на таблицю з лічильником; перемикання міняє ?tab=', async () => {
    await open();
    const tabs = within(screen.getByRole('tablist', { name: 'Permit sections' })).getAllByRole('tab');
    expect(tabs.map((tab) => tab.textContent)).toEqual(['Overview', 'Limits2', 'Places1']);
    expect(url()).toBe('/admin/permits/5');

    fireEvent.click(tabs[1]!);
    expect(await screen.findByText('Outfall A')).toBeTruthy();
    expect(url()).toBe('/admin/permits/5?tab=Limits');
    expect(screen.getByText('Sum of all 2 rows')).toBeTruthy();

    fireEvent.click(tabs[0]!);
    expect(await screen.findByRole('form', { name: 'Permit details' })).toBeTruthy();
    expect(url()).toBe('/admin/permits/5');
  });

  it('невідома вкладка в адресі відкриває Overview', async () => {
    await open('/admin/permits/5?tab=Nope');
    expect(screen.getByRole('form', { name: 'Permit details' })).toBeTruthy();
  });

  it('таблиця читається з реєстру PRM_… з батьком-дозволом', async () => {
    await openLimits();
    expect(getRows).toHaveBeenCalledWith(
      LimitsRegistry,
      expect.objectContaining({ parentEntryId: 5, asOf: expect.stringMatching(/^\d{4}-\d{2}-\d{2}$/) as string }),
    );
  });

  it('порожня таблиця: підказка додати рядок; для закінченого — що лише читання', async () => {
    getRows.mockResolvedValue(page([]));
    await open('/admin/permits/5?tab=Limits');
    expect(await screen.findByText('No rows in Limits yet')).toBeTruthy();
    expect(screen.getByText('Add the first row by hand, or paste a block of rows from Excel.')).toBeTruthy();
    cleanup();

    await open('/admin/permits/5?tab=Limits', permit({ status: 'Expired' }));
    expect(await screen.findByText('This permit is read-only here, so rows cannot be added.')).toBeTruthy();
  });

  it('Overview: підсумки таблиць, Read by без листів, смуга чернетки', async () => {
    await open();
    expect(screen.getByText('Draft - sheets do not read this permit yet')).toBeTruthy();
    expect(screen.getByText('No sheet reads this permit yet.')).toBeTruthy();
    expect(screen.getByText('30')).toBeTruthy();
    expect(screen.getByText('2 rows')).toBeTruthy();
  });
});

describe('життєвий цикл за станом і правами', () => {
  const buttons = (): string[] => screen.queryAllByRole('button').map((button) => button.textContent ?? '');

  it('чернетка: Activate… і меню з Delete draft…; Renew… немає', async () => {
    await open();
    expect(buttons()).toContain('Activate…');
    expect(buttons()).not.toContain('Renew…');
    fireEvent.click(screen.getByRole('button', { name: 'More' }));
    expect(await screen.findByRole('menuitem', { name: 'Delete draft…' })).toBeTruthy();
    expect(screen.queryByRole('menuitem', { name: 'End this permit early…' })).toBeNull();
  });

  it('чинний: Renew… і меню з End this permit early…; Activate… немає', async () => {
    await open('/admin/permits/5', permit({ status: 'Active', endsInDays: 400 }));
    expect(buttons()).toContain('Renew…');
    expect(buttons()).not.toContain('Activate…');
    fireEvent.click(screen.getByRole('button', { name: 'More' }));
    expect(await screen.findByRole('menuitem', { name: 'End this permit early…' })).toBeTruthy();
    expect(screen.queryByRole('menuitem', { name: 'Delete draft…' })).toBeNull();
  });

  it('закінчений: лише Renew…; без права запису — жодної дії', async () => {
    await open('/admin/permits/5', permit({ status: 'Expired' }));
    expect(buttons()).toContain('Renew…');
    expect(screen.queryByRole('button', { name: 'More' })).toBeNull();
    cleanup();

    session.permissions = Viewer;
    await open('/admin/permits/5', permit({ status: 'Active' }));
    for (const label of ['Renew…', 'Activate…', 'More']) expect(buttons()).not.toContain(label);
  });

  it('скорий кінець чинного: смуга з Renew…', async () => {
    await open('/admin/permits/5', permit({ status: 'Active', endsInDays: 30 }));
    expect(screen.getByText(/^Ends in 30 days/)).toBeTruthy();
  });

  it('?dialog= для недоступної дії ігнорується', async () => {
    await open('/admin/permits/5?dialog=end');
    expect(screen.queryByRole('dialog')).toBeNull();
  });

  it('Activate: підтвердження викликає activatePermit(5)', async () => {
    activatePermit.mockResolvedValue(permit({ status: 'Active' }));
    await open();
    fireEvent.click(screen.getAllByRole('button', { name: 'Activate…' })[0]!);
    const dialog = await screen.findByRole('dialog', { name: 'Activate permit KZ-100?' });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Activate' }));
    await waitFor(() => expect(activatePermit).toHaveBeenCalledWith(5));
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
  });

  it('відмова сервера не закриває екран: дозвіл перечитується, діалог знято', async () => {
    activatePermit.mockRejectedValue(new EcrApiError({ status: 409, title: 't', errorCode: 'ECR-PRM-4092', messageKey: 'err.ECR-PRM-4092.overlap', detail: 'x' } as never));
    await open();
    fireEvent.click(screen.getAllByRole('button', { name: 'Activate…' })[0]!);
    fireEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Activate' }));
    await waitFor(() => expect(activatePermit).toHaveBeenCalled());
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    expect(getPermit.mock.calls.length).toBeGreaterThan(1);
  });

  it('End: причина від 10 символів, endDate — null (сьогодні на сервері)', async () => {
    endPermit.mockResolvedValue(permit({ status: 'Expired' }));
    await open('/admin/permits/5', permit({ status: 'Active', endsInDays: 400 }));
    fireEvent.click(screen.getByRole('button', { name: 'More' }));
    fireEvent.click(await screen.findByRole('menuitem', { name: 'End this permit early…' }));
    const dialog = await screen.findByRole('dialog');
    const confirm = within(dialog).getByRole('button', { name: 'End permit' });
    const reason = within(dialog).getByRole('textbox', { name: /Reason/ });

    fireEvent.change(reason, { target: { value: 'short' } });
    expect(confirm).toHaveProperty('disabled', true);
    fireEvent.change(reason, { target: { value: 'Operations stopped for good' } });
    expect(confirm).toHaveProperty('disabled', false);
    fireEvent.click(confirm);
    await waitFor(() => expect(endPermit).toHaveBeenCalledWith(5, { endDate: null, reason: 'Operations stopped for good' }));
  });

  it('Renew відкриває нову чернетку; Delete draft повертає до переліку', async () => {
    renewPermit.mockResolvedValue(permit({ id: 6, status: 'Draft' }));
    await open('/admin/permits/5', permit({ status: 'Active', endsInDays: 400 }));
    fireEvent.click(screen.getByRole('button', { name: 'Renew…' }));
    fireEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Create renewal' }));
    await waitFor(() => expect(url()).toBe('/admin/permits/6'));
    cleanup();

    deletePermit.mockResolvedValue(undefined);
    await open();
    fireEvent.click(screen.getByRole('button', { name: 'More' }));
    fireEvent.click(await screen.findByRole('menuitem', { name: 'Delete draft…' }));
    fireEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Delete draft' }));
    await waitFor(() => expect(deletePermit).toHaveBeenCalledWith(5));
    await waitFor(() => expect(url()).toBe('/admin/permits'));
  });
});

describe('форма рядка', () => {
  const nameBox = () => screen.getByRole('textbox', { name: /^Name/ });
  const annualBox = () => screen.getByRole('textbox', { name: /^Annual volume/ });

  async function addRow(): Promise<void> {
    await openLimits();
    fireEvent.click(screen.getByRole('button', { name: 'Add row' }));
    await screen.findByRole('dialog', { name: 'New row' });
  }

  it('порожній рядок: обов’язкові позначено, пакет не йде', async () => {
    await addRow();
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));
    expect(await screen.findByText('Fill in the required fields.')).toBeTruthy();
    expect(screen.getAllByText('Required').length).toBeGreaterThanOrEqual(2);
    expect(saveBatch).not.toHaveBeenCalled();
  });

  it('дубль ключа проти наявних рядків: повідомлення зі стовпцями, пакет не йде', async () => {
    await addRow();
    fireEvent.change(nameBox(), { target: { value: 'Outfall A' } });
    fireEvent.change(annualBox(), { target: { value: '1' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));
    expect(await screen.findByText('This permit already has a row with the same Name.')).toBeTruthy();
    expect(saveBatch).not.toHaveBeenCalled();
  });

  it('нечислове значення — помилка біля поля', async () => {
    await addRow();
    fireEvent.change(nameBox(), { target: { value: 'C' } });
    fireEvent.change(annualBox(), { target: { value: '12abc' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));
    expect(await screen.findByText('Enter a number, for example 12.5.')).toBeTruthy();
    expect(saveBatch).not.toHaveBeenCalled();
  });

  it('новий рядок: PERMIT = id дозволу, число з комою іде з крапкою, dryRun=false', async () => {
    await addRow();
    fireEvent.change(nameBox(), { target: { value: 'Outfall C' } });
    fireEvent.change(annualBox(), { target: { value: '5,5' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));

    await waitFor(() => expect(saveBatch).toHaveBeenCalledTimes(1));
    expect(saveBatch).toHaveBeenCalledWith(
      LimitsRegistry,
      [{ clientRowId: 'save', op: 'upsert', id: null, code: null, baseVersion: null, values: { PERMIT: '5', Name: 'Outfall C', Annual: '5.5', Active: 'false' } }],
      false,
    );
    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'New row' })).toBeNull());
  });

  it('відмова сервера по полю лишає шторку відкритою й показує текст біля поля', async () => {
    saveBatch.mockResolvedValue({
      added: 0,
      applied: false,
      deleted: 0,
      dryRun: false,
      rows: [{ clientRowId: 'save', entryId: null, errors: [{ errorCode: 'ECR-REG-4221', field: 'Name', messageKey: 'permits.page.row.required', params: {} }], status: 'error', version: null }],
      unchanged: 0,
      updated: 0,
    });
    await addRow();
    fireEvent.change(nameBox(), { target: { value: 'Outfall C' } });
    fireEvent.change(annualBox(), { target: { value: '1' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));
    expect(await screen.findByText('The row was not saved. Check the fields and try again.')).toBeTruthy();
    expect(screen.getByRole('dialog', { name: 'New row' })).toBeTruthy();
  });

  it('наявний рядок: форма заповнена, у пакеті лише змінене поле з baseVersion', async () => {
    await openLimits();
    fireEvent.click(screen.getByText('Outfall A'));
    const dialog = await screen.findByRole('dialog', { name: 'Outfall A' });
    expect(within(dialog).getByRole('textbox', { name: /^Annual volume/ })).toHaveProperty('value', '10');

    fireEvent.change(within(dialog).getByRole('textbox', { name: /^Annual volume/ }), { target: { value: '11,5' } });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(saveBatch).toHaveBeenCalledTimes(1));
    expect(saveBatch).toHaveBeenCalledWith(
      LimitsRegistry,
      [{ clientRowId: 'save', op: 'upsert', id: 11, code: null, baseVersion: 'v11', values: { Annual: '11.5' } }],
      false,
    );
  });

  it('рядок сам із собою не конфліктує за ключем', async () => {
    await openLimits();
    fireEvent.click(screen.getByText('Outfall B'));
    const dialog = await screen.findByRole('dialog', { name: 'Outfall B' });
    fireEvent.change(within(dialog).getByRole('textbox', { name: /^Annual volume/ }), { target: { value: '21' } });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(saveBatch).toHaveBeenCalledTimes(1));
    expect(screen.queryByText(/already has a row/)).toBeNull();
  });

  it('видалення: пакет delete з id і токеном', async () => {
    await openLimits();
    fireEvent.click(screen.getByText('Outfall B'));
    const dialog = await screen.findByRole('dialog', { name: 'Outfall B' });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Delete row' }));
    await waitFor(() => expect(saveBatch).toHaveBeenCalledWith(LimitsRegistry, [{ clientRowId: 'delete', op: 'delete', id: 12, code: null, baseVersion: 'v12', values: null }], false));
  });

  it('список «таблиця цього дозволу» бере підписи з рядків сестринської таблиці', async () => {
    await openLimits();
    fireEvent.click(screen.getByText('Outfall A'));
    const dialog = await screen.findByRole('dialog', { name: 'Outfall A' });
    const place = await within(dialog).findByRole('textbox', { name: /^Place/ });
    expect(place).toHaveProperty('value', 'Pond 1');
    expect(within(dialog).getByText('A table of this permit: Places. Add the row there first.')).toBeTruthy();
    expect(getRows).toHaveBeenCalledWith('PRM_WD_Places', expect.objectContaining({ parentEntryId: 5 }));
    expect(getRows).toHaveBeenCalledWith('Substances', expect.not.objectContaining({ parentEntryId: expect.anything() }));
  });

  it('закінчений дозвіл: рядок відкривається лише для читання', async () => {
    await openLimits(permit({ status: 'Expired' }));
    expect(screen.getByRole('button', { name: 'Add row' })).toHaveProperty('disabled', true);
    fireEvent.click(screen.getByText('Outfall A'));
    const dialog = await screen.findByRole('dialog', { name: 'Outfall A' });
    expect(within(dialog).queryByRole('button', { name: 'Save' })).toBeNull();
    expect(within(dialog).queryByRole('button', { name: 'Delete row' })).toBeNull();
    expect(within(dialog).getByRole('textbox', { name: /^Annual volume/ })).toHaveProperty('readOnly', true);
  });

  it('глядач без права запису: кнопки таблиці вимкнені', async () => {
    session.permissions = Viewer;
    await openLimits(permit({ status: 'Active' }));
    for (const name of ['Add row', 'Paste from Excel…', 'Import file…']) {
      expect(screen.getByRole('button', { name })).toHaveProperty('disabled', true);
    }
  });
});

describe('вставка з Excel', () => {
  const rowsText = [
    ['Outfall C', '5,5', '1', 'так', '01.03.2026', 'Pond 1', 'Cooling', 'Methane'].join('\t'),
    ['Outfall D', '7', '', '', '', '', '', ''].join('\t'),
  ].join('\n');

  async function openPaste(): Promise<HTMLElement> {
    await openLimits();
    fireEvent.click(screen.getByRole('button', { name: 'Paste from Excel…' }));
    const dialog = await screen.findByRole('dialog', { name: 'Paste rows into Limits' });
    // Пікери читаються асинхронно: поки йдуть, кнопка «Check rows» вимкнена.
    await waitFor(() => expect(getRows).toHaveBeenCalledWith('Substances', expect.anything()));
    return dialog;
  }

  async function fill(dialog: HTMLElement, text: string): Promise<void> {
    fireEvent.change(within(dialog).getByRole('textbox', { name: 'Rows' }), { target: { value: text } });
  }

  it('розбір → перевірка dryRun → запис без dryRun; лише готові рядки', async () => {
    saveBatch.mockImplementation((_code, items, dryRun) =>
      Promise.resolve({
        added: items.length,
        applied: !dryRun,
        deleted: 0,
        dryRun,
        rows: items.map((item) => ({ clientRowId: item.clientRowId, entryId: null, errors: [], status: 'added', version: null })),
        unchanged: 0,
        updated: 0,
      }),
    );
    const dialog = await openPaste();
    await fill(dialog, rowsText);
    expect(await within(dialog).findByText('2 rows can be added to Limits; 0 need fixing before anything is added.')).toBeTruthy();

    const check = within(dialog).getByRole('button', { name: 'Check rows' });
    await waitFor(() => expect(check).toHaveProperty('disabled', false));
    fireEvent.click(check);
    expect(await within(dialog).findByText('2 rows pass the check, 0 are rejected by the server')).toBeTruthy();
    expect(saveBatch.mock.calls[0]?.[2]).toBe(true);

    fireEvent.click(within(dialog).getByRole('button', { name: 'Add 2 rows' }));
    // Перевірка → (повторна перевірка всіх шматків перед записом) → запис.
    await waitFor(() => expect(saveBatch).toHaveBeenCalledTimes(3));
    expect(saveBatch.mock.calls.map((call) => call[2])).toEqual([true, true, false]);
    const sent = saveBatch.mock.calls[2]?.[1] ?? [];
    expect(sent.map((item) => item.values)).toEqual([
      { PERMIT: '5', Name: 'Outfall C', Annual: '5.5', Count: '1', Active: 'true', Issued: '2026-03-01', Place: '31', Use: '21', Substance: '41' },
      { PERMIT: '5', Name: 'Outfall D', Annual: '7' },
    ]);
    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Paste rows into Limits' })).toBeNull());
  });

  it('нерозпізнана клітинка названа; «все або нічого»: перевірка й додавання недоступні, нічого не пишеться', async () => {
    const dialog = await openPaste();
    await fill(dialog, ['Outfall C', '12abc'].join('\t') + '\n' + ['Outfall D', '7'].join('\t'));
    expect(await within(dialog).findByText('1 rows can be added to Limits; 1 need fixing before anything is added.')).toBeTruthy();
    expect(within(dialog).getByText('Row 1, Annual volume: "12abc" is not a number')).toBeTruthy();
    expect(within(dialog).getByText('Nothing is added while there are problems. Fix them and check the rows again.')).toBeTruthy();
    expect(within(dialog).getByRole('button', { name: 'Check rows' })).toHaveProperty('disabled', true);
    expect(within(dialog).getByRole('button', { name: 'Add 0 rows' })).toHaveProperty('disabled', true);
    expect(saveBatch).not.toHaveBeenCalled();
  });

  it('понад 5000 рядків: причина названа, «Check rows» і «Add» недоступні, нічого не пишеться', async () => {
    const dialog = await openPaste();
    await fill(dialog, Array.from({ length: 5001 }, (_, index) => `N${String(index)}\t1`).join('\n'));
    expect(await within(dialog).findByText('More than 5000 rows: nothing is added. Split the data into several parts.')).toBeTruthy();
    expect(within(dialog).getByRole('button', { name: 'Check rows' })).toHaveProperty('disabled', true);
    expect(within(dialog).getByRole('button', { name: 'Add 0 rows' })).toHaveProperty('disabled', true);
    expect(saveBatch).not.toHaveBeenCalled();
  });

  it('CSV більший за 5 МБ не читається: повідомлення біля поля, текст порожній', async () => {
    const dialog = await openPaste();
    const big = new File(['Name;Annual\nA;1\n'], 'big.csv', { type: 'text/csv' });
    Object.defineProperty(big, 'size', { value: 5 * 1024 * 1024 + 1 });
    const input = dialog.querySelector('input[type="file"]');
    await userEvent.setup({ applyAccept: false }).upload(input as HTMLInputElement, big);
    expect(await within(dialog).findByText('The file is larger than 5 MB, so nothing was loaded. Split it into several files.')).toBeTruthy();
    expect(within(dialog).getByRole('textbox', { name: 'Rows' })).toHaveProperty('value', '');
    expect(within(dialog).getByText('Nothing to add yet')).toBeTruthy();
  });

  it('відмова середнього шматка під час запису: банер «зупинено», шматок до нього лишається записаним', async () => {
    saveBatch.mockImplementation((_code, items, dryRun) => {
      const second = items[0]?.clientRowId === 'row-501';
      const refused = !dryRun && second;
      return Promise.resolve({
        added: 0,
        applied: !dryRun && !refused,
        deleted: 0,
        dryRun,
        rows: items.map((item) => ({
          clientRowId: item.clientRowId,
          entryId: null,
          errors: refused ? [{ errorCode: 'ECR-REG-4221', field: 'Name', messageKey: 'permits.page.row.required', params: {} }] : [],
          status: 'added',
          version: null,
        })),
        unchanged: 0,
        updated: 0,
      });
    });
    const dialog = await openPaste();
    await fill(dialog, Array.from({ length: 501 }, (_, index) => `N${String(index)}\t1`).join('\n'));
    const check = within(dialog).getByRole('button', { name: 'Check rows' });
    await waitFor(() => expect(check).toHaveProperty('disabled', false));
    fireEvent.click(check);
    fireEvent.click(await within(dialog).findByRole('button', { name: 'Add 501 rows' }));
    expect(await within(dialog).findByText('Stopped after saving 500 rows')).toBeTruthy();
    expect(saveBatch.mock.calls.map((call) => call[2])).toEqual([true, true, true, true, false, false]);
  });

  it('відхилений сервером рядок блокує весь запис: Add недоступна, не записано нічого', async () => {
    saveBatch.mockImplementation((_code, items, dryRun) =>
      Promise.resolve({
        added: 0,
        applied: !dryRun,
        deleted: 0,
        dryRun,
        rows: items.map((item, index) => ({
          clientRowId: item.clientRowId,
          entryId: null,
          errors: dryRun && index === 0 ? [{ errorCode: 'ECR-REG-4221', field: 'Name', messageKey: 'permits.page.row.required', params: {} }] : [],
          status: 'added',
          version: null,
        })),
        unchanged: 0,
        updated: 0,
      }),
    );
    const dialog = await openPaste();
    await fill(dialog, ['A', '1'].join('\t') + '\n' + ['B', '2'].join('\t'));
    const check = within(dialog).getByRole('button', { name: 'Check rows' });
    await waitFor(() => expect(check).toHaveProperty('disabled', false));
    fireEvent.click(check);
    expect(await within(dialog).findByText('0 rows pass the check, 1 are rejected by the server')).toBeTruthy();
    expect(within(dialog).getByText('Row 1: Required')).toBeTruthy();
    expect(within(dialog).getByText('Nothing is added while there are problems. Fix them and check the rows again.')).toBeTruthy();

    expect(within(dialog).getByRole('button', { name: 'Add 0 rows' })).toHaveProperty('disabled', true);
    expect(saveBatch.mock.calls.every((call) => call[2] === true)).toBe(true);
  });

  it('CSV-файл: крапка з комою, заголовок пропущено', async () => {
    const dialog = await openPaste();
    const file = new File(['Name;Annual\nOutfall C;5,5\n'], 'rows.csv', { type: 'text/csv' });
    const input = dialog.querySelector('input[type="file"]');
    expect(input).not.toBeNull();
    await userEvent.setup().upload(input as HTMLInputElement, file);
    expect(await within(dialog).findByText('1 rows can be added to Limits; 0 need fixing before anything is added.')).toBeTruthy();
    expect(within(dialog).getByRole('checkbox', { name: 'The first line is a header (skip it)' })).toHaveProperty('checked', true);
  });

  it('порожній ввід: нічого додавати', async () => {
    const dialog = await openPaste();
    expect(within(dialog).getByText('Nothing to add yet')).toBeTruthy();
    expect(within(dialog).getByRole('button', { name: 'Check rows' })).toHaveProperty('disabled', true);
  });
});

describe('стани завантаження', () => {
  it('404 дозволу — повідомлення про відмову, а не порожній екран', async () => {
    getPermit.mockRejectedValue(new EcrApiError({ status: 404, title: 'Not found', errorCode: 'ECR-PRM-0404', detail: 'x' } as never));
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <TestProviders>
        <QueryClientProvider client={client}>
          <MemoryRouter initialEntries={['/admin/permits/77']}>
            <Routes>
              <Route path="/admin/permits/:id" element={<PermitPage />} />
            </Routes>
          </MemoryRouter>
        </QueryClientProvider>
      </TestProviders>,
    );
    expect(await screen.findByRole('link', { name: '← Back to Permits' })).toBeTruthy();
    expect(screen.queryByRole('tablist')).toBeNull();
  });
});
