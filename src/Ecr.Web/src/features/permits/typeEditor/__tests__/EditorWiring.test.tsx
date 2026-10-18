import type { JSX } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom';
import { apiFetch } from '@/api/client';
import { PermitTypeEditorPage } from '@/pages/admin/PermitTypeEditorPage';
import { showDone, showWarning } from '@/shared/ui/notify';
import { Catalog } from '@/test/__tests__/a11yFixtures';
import { TestProviders } from '@/test/render';
import * as typesApi from '../../api/typesApi';
import { permitType } from './fixtures';

/**
 * Зведення PM-08b (сторінка) + PM-08c (діалоги, вкладки): права на «Add check», `flush` перед
 * публікацією з найсвіжішим `rowVersion`, видалення таблиці й типу. Тексти — справжні, із сіду.
 *
 * Мутаційний доказ (виконано вручну, відновлено): у `DialogsHost.settle` прибрати `await api.flush()` валить
 * «публікація спершу зберігає чернетку»; у `TypeEditorTabs` `canEdit={editable && mayEdit}` → `canEdit` без
 * `mayEdit` валить «без права кнопки «Add check» немає»; у `DeleteTableDialog` `confirmDisabled` без
 * `publishedBlock` валить «опублікований тип: збережену таблицю не видалити».
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
vi.mock('../../api/typesApi', () => ({
  getPermitType: vi.fn(),
  replacePermitType: vi.fn(),
  publishPermitType: vi.fn(),
  openPermitTypeVersion: vi.fn(),
  deletePermitType: vi.fn(),
}));
const session = vi.hoisted(() => ({ permissions: ['Registry.EditDefinition'] as string[] }));
vi.mock('@/shared/session/useSession', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/session/useSession')>()),
  useSession: () => ({ data: { permissions: session.permissions, isSimulation: false } }),
}));

vi.mock('@/shared/ui/notify', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/ui/notify')>()),
  showDone: vi.fn(),
  showWarning: vi.fn(),
  showApiError: vi.fn(),
}));

const get = vi.mocked(typesApi.getPermitType);
const put = vi.mocked(typesApi.replacePermitType);
const publish = vi.mocked(typesApi.publishPermitType);
const remove = vi.mocked(typesApi.deletePermitType);

beforeEach(() => {
  session.permissions = ['Registry.EditDefinition'];
  vi.mocked(apiFetch).mockImplementation((path: string) =>
    Promise.resolve(path === '/api/v1/units' ? [{ id: 7, code: 't/yr' }] : []) as never,
  );
});
afterEach(() => {
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
            <Route path="/admin/permits/types" element={<Probe />} />
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

const url = (): string => screen.getByTestId('url').textContent ?? '';
const params = (): URLSearchParams => new URLSearchParams(url().split('?')[1] ?? '');

describe('вкладка Checks: право поточного користувача', () => {
  it('є право і чернетка: «Add check» відкриває діалог (?dialog=add-rule)', async () => {
    await open('/admin/permits/types/WaterDischarge?tab=rules');
    fireEvent.click(await screen.findByRole('button', { name: 'Add check' }));
    expect(params().get('dialog')).toBe('add-rule');
    expect(await screen.findByRole('dialog', { name: 'Add a check' })).toBeTruthy();
  });

  it('без права кнопки «Add check» немає', async () => {
    session.permissions = ['Registry.View'];
    await open('/admin/permits/types/WaterDischarge?tab=rules');
    expect(await screen.findByText('No checks yet.')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Add check' })).toBeNull();
  });

  it('опублікований тип без чернетки: кнопки «Add check» немає', async () => {
    await open(
      '/admin/permits/types/WaterDischarge?tab=rules',
      permitType({ status: 'Published', hasUnpublishedChanges: false, draftVersion: null, version: 2 }),
    );
    expect(await screen.findByText('No checks yet.')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Add check' })).toBeNull();
  });

  it('додана перевірка лягає в робочу копію: лічильник вкладки й «1 unsaved changes»', async () => {
    const user = userEvent.setup();
    await open('/admin/permits/types/WaterDischarge?tab=rules&dialog=add-rule');
    const dialog = await screen.findByRole('dialog', { name: 'Add a check' });
    await user.type(within(dialog).getByLabelText(/What it checks/), 'Annual is not negative');
    await user.type(within(dialog).getByLabelText(/^Rule/), 'ROW.Annual >= 0');
    await user.click(within(dialog).getByRole('button', { name: 'Add check' }));

    expect(await screen.findByText('Annual is not negative')).toBeTruthy();
    expect(screen.getByRole('tab', { name: /Checks/ }).textContent).toBe('Checks1');
    expect(screen.getByText('1 unsaved changes')).toBeTruthy();
    expect(params().has('dialog')).toBe(false);
  });
});

describe('публікація', () => {
  async function dirtyThenPublishDialog(): Promise<ReturnType<typeof userEvent.setup>> {
    const user = userEvent.setup();
    await open('/admin/permits/types/WaterDischarge?panel=Annual');
    const inspector = await screen.findByTestId('column-inspector');
    await user.clear(within(inspector).getByLabelText(/Name \(English\)/));
    await user.type(within(inspector).getByLabelText(/Name \(English\)/), 'Yearly');
    await user.click(screen.getByRole('button', { name: 'Publish…' }));
    await user.type(await screen.findByLabelText(/What changed/), 'Renamed the annual column');
    return user;
  }

  it('публікація спершу зберігає чернетку, шле найсвіжіший rowVersion і приймає відповідь сервера', async () => {
    put.mockResolvedValue(permitType({ rowVersion: 'rv-2' }));
    const published = permitType({
      status: 'Published',
      version: 1,
      draftVersion: null,
      hasUnpublishedChanges: false,
      rowVersion: 'rv-3',
    });
    publish.mockResolvedValue(published);
    const user = await dirtyThenPublishDialog();
    // Після успіху скидається домен `['permits','types']`: перечитування бачить уже опублікований тип.
    get.mockResolvedValue(published);
    await user.click(screen.getByRole('button', { name: 'Publish' }));

    await waitFor(() => expect(publish).toHaveBeenCalledTimes(1));
    expect(put).toHaveBeenCalledTimes(1);
    expect(put.mock.invocationCallOrder[0]).toBeLessThan(publish.mock.invocationCallOrder[0]!);
    expect(publish).toHaveBeenCalledWith('WaterDischarge', { reason: 'Renamed the annual column', rowVersion: 'rv-2' });
    expect(await screen.findByText('Published v1')).toBeTruthy();
    await waitFor(() => expect(params().has('dialog')).toBe(false));
    expect(vi.mocked(showDone)).toHaveBeenCalledTimes(1);
  });

  it('копія з вадами не зберігається, а отже й не публікується: запиту немає', async () => {
    const user = userEvent.setup();
    await open('/admin/permits/types/WaterDischarge?t=Storage&panel=Place');
    const inspector = await screen.findByTestId('column-inspector');
    await user.click(within(inspector).getByRole('switch', { name: /unique key/ }));
    await user.click(screen.getByRole('button', { name: 'Publish…' }));
    await user.type(await screen.findByLabelText(/What changed/), 'Dropped the storage key');
    await user.click(screen.getByRole('button', { name: 'Publish' }));

    await waitFor(() => expect(screen.getByRole('button', { name: 'Publish' })).toBeTruthy());
    expect(put).not.toHaveBeenCalled();
    expect(publish).not.toHaveBeenCalled();
    expect(showWarning).toHaveBeenCalledWith(expect.stringContaining('nothing was sent'));
  });
});

describe('видалення таблиці', () => {
  const opened = '/admin/permits/types/WaterDischarge?dialog=delete-table&table=Storage';

  it('тип ніколи не публікувався: підтвердження прибирає таблицю з копії', async () => {
    await open(opened);
    const dialog = await screen.findByRole('dialog', { name: 'Remove table "Storage"?' });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Remove table' }));

    await waitFor(() => expect(screen.queryByRole('button', { name: /^Storage/ })).toBeNull());
    expect(screen.getByText('1 unsaved changes')).toBeTruthy();
    expect(params().has('dialog')).toBe(false);
  });

  it('опублікований тип: збережену таблицю не видалити, текст — з каталогу відмови сервера', async () => {
    await open(opened, permitType({ version: 2, draftVersion: 3 }));
    const dialog = await screen.findByRole('dialog', { name: 'Remove table "Storage"?' });
    expect(within(dialog).getByText(/Table "Storage" is published and cannot be removed\./)).toBeTruthy();
    expect((within(dialog).getByRole('button', { name: 'Remove table' }) as HTMLButtonElement).disabled).toBe(true);
  });

  it('на таблицю посилається список іншої: видалення заблоковане, названо стовпець', async () => {
    const type = permitType();
    type.tables[0]!.columns[0] = {
      ...type.tables[0]!.columns[0]!,
      dataType: 'Lookup',
      listScope: 'SamePermitTable',
      siblingTableCode: 'Storage',
    };
    await open(opened, type);
    const dialog = await screen.findByRole('dialog', { name: 'Remove table "Storage"?' });
    expect(within(dialog).getByText(/Discharge\.Water_body/)).toBeTruthy();
    expect((within(dialog).getByRole('button', { name: 'Remove table' }) as HTMLButtonElement).disabled).toBe(true);
  });
});

describe('видалення типу', () => {
  it('DELETE і перехід на перелік типів', async () => {
    remove.mockResolvedValue(undefined);
    const user = userEvent.setup();
    await open();
    await user.click(screen.getByRole('button', { name: 'More actions' }));
    await user.click(await screen.findByRole('menuitem', { name: 'Delete type…' }));
    await user.click(await screen.findByRole('button', { name: 'Delete type' }));

    await waitFor(() => expect(remove).toHaveBeenCalledWith('WaterDischarge'));
    await waitFor(() => expect(url()).toBe('/admin/permits/types'));
  });
});
