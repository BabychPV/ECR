import type { JSX } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom';
import { apiFetch } from '@/api/client';
import { PermitTypeEditorPage } from '@/pages/admin/PermitTypeEditorPage';
import { Catalog } from '@/test/__tests__/a11yFixtures';
import { TestProviders } from '@/test/render';
import * as typesApi from '../../api/typesApi';
import { permitType } from './fixtures';

/**
 * Права конструктора типу (аудит ч.1, P2-1 і P2-2): запис потребує `Permit.EditType` поза
 * симуляцією, публікація — ще й `Permit.PublishType`; адреса `?dialog=` не відкриває діалогів без права.
 *
 * Мутаційний доказ (виконано вручну, відновлено): у `TypeEditorHeader` `writable = editable && mayEdit` →
 * `writable = editable` валить «без права» і «симуляція»; прибрати `disabled={!mayPublish}` валить «без права
 * на публікацію»; у `TypeEditorDialogs` `denied` завжди `false` валить «?dialog=» без права; у `StructureTab`
 * `editable = api.editable` валить «без права: структура лише для читання».
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
const session = vi.hoisted(() => ({ permissions: [] as string[], isSimulation: false }));
vi.mock('@/shared/session/useSession', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/session/useSession')>()),
  useSession: () => ({ data: { permissions: session.permissions, isSimulation: session.isSimulation } }),
}));

const Edit = 'Permit.EditType';
const Publish = 'Permit.PublishType';

beforeEach(() => {
  session.permissions = [Edit, Publish];
  session.isSimulation = false;
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

async function open(url: string, type = permitType()) {
  vi.mocked(typesApi.getPermitType).mockResolvedValue(type);
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

const base = '/admin/permits/types/WaterDischarge';
const params = (): URLSearchParams =>
  new URLSearchParams((screen.getByTestId('url').textContent ?? '').split('?')[1] ?? '');
const button = (name: string | RegExp): HTMLButtonElement | null =>
  screen.queryByRole('button', { name }) as HTMLButtonElement | null;

/** Усе, що пише: шапка, банер/кнопки структури, дерево. */
const WriteButtons = ['Check', 'Publish…', 'More actions', 'Add column', 'Add table…', 'Add table'];

function expectNoWriteButtons(): void {
  for (const name of [...WriteButtons, 'New version…', 'Add another table']) {
    expect(button(name), name).toBeNull();
  }
}

describe('право на запис (P2-2)', () => {
  it('є право: шапка й структура редагуються', async () => {
    await open(`${base}?panel=Annual`);
    // Ті самі імена, що в тесті «без права»: порожній пошук там не мав би сили.
    for (const name of WriteButtons) expect(button(name), name).not.toBeNull();
    expect(button('Publish…')?.disabled).toBe(false);
    const inspector = await screen.findByTestId('column-inspector');
    expect((within(inspector).getByLabelText(/Name \(English\)/) as HTMLInputElement).disabled).toBe(false);
  });

  it('без права: жодної кнопки запису, інспектор лише для читання, «New version» немає', async () => {
    session.permissions = ['Registry.View'];
    await open(`${base}?panel=Annual`);
    expectNoWriteButtons();
    const inspector = await screen.findByTestId('column-inspector');
    expect((within(inspector).getByLabelText(/Name \(English\)/) as HTMLInputElement).disabled).toBe(true);
  });

  it('симуляція при повному наборі прав: так само лише читання', async () => {
    session.isSimulation = true;
    await open(`${base}?panel=Annual`);
    expectNoWriteButtons();
    const inspector = await screen.findByTestId('column-inspector');
    expect((within(inspector).getByLabelText(/Name \(English\)/) as HTMLInputElement).disabled).toBe(true);
  });

  it('опублікований тип: «New version» є лише з правом (шапка й банер)', async () => {
    const published = permitType({ status: 'Published', hasUnpublishedChanges: false, draftVersion: null, version: 2 });
    await open(base, published);
    expect(screen.getAllByRole('button', { name: 'New version…' })).toHaveLength(2);
    cleanup();

    session.permissions = ['Registry.View'];
    await open(base, published);
    expect(screen.queryAllByRole('button', { name: 'New version…' })).toHaveLength(0);
  });
});

describe('право на публікацію (P2-1)', () => {
  it('є лише право на правку: «Publish…» вимкнена, причина видна в title', async () => {
    session.permissions = [Edit];
    await open(base);
    const publish = button('Publish…');
    expect(publish?.disabled).toBe(true);
    expect(publish?.getAttribute('title')).toBe(Catalog['permits.editor.actions.publishNoRight']);
    expect(button('Check')).not.toBeNull();
  });

  it('є обидва права: «Publish…» активна й без title', async () => {
    await open(base);
    expect(button('Publish…')?.disabled).toBe(false);
    expect(button('Publish…')?.hasAttribute('title')).toBe(false);
  });
});

describe('?dialog= в адресі без права', () => {
  it('publish-type без права на публікацію: діалогу немає, параметр прибрано', async () => {
    session.permissions = [Edit];
    await open(`${base}?dialog=publish-type`);
    await waitFor(() => expect(params().has('dialog')).toBe(false));
    expect(screen.queryByRole('dialog')).toBeNull();
  });

  it('publish-type з правом: діалог відкрито', async () => {
    await open(`${base}?dialog=publish-type`);
    expect(await screen.findByRole('dialog')).toBeTruthy();
    expect(params().get('dialog')).toBe('publish-type');
  });

  it.each(['add-table', 'add-rule', 'new-version', 'delete-type', 'publish-type'])(
    '%s без права на правку (і з правом на публікацію): діалогу немає, параметр прибрано',
    async (dialog) => {
      session.permissions = [Publish];
      await open(`${base}?dialog=${dialog}`);
      await waitFor(() => expect(params().has('dialog')).toBe(false));
      expect(screen.queryByRole('dialog')).toBeNull();
    },
  );

  it('delete-table без права: діалогу немає, параметри діалогу прибрано', async () => {
    session.permissions = ['Registry.View'];
    await open(`${base}?dialog=delete-table&table=Storage`);
    await waitFor(() => expect(params().has('dialog')).toBe(false));
    expect(params().has('table')).toBe(false);
    expect(screen.queryByRole('dialog')).toBeNull();
  });

  it('симуляція: діалог запису з адреси теж не відкривається', async () => {
    session.isSimulation = true;
    await open(`${base}?dialog=add-table`);
    await waitFor(() => expect(params().has('dialog')).toBe(false));
    expect(screen.queryByRole('dialog')).toBeNull();
  });
});
