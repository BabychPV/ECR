import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom';
import { testTheme } from '@/test/render';
import { PermitListsPage } from '@/pages/admin/PermitListsPage';
import type { PermitListDetail, PermitListDto, PermitListValue } from '../listsApi';

/**
 * Екран «Lists» (PM-08d, PM-08e): дані з одного ендпоінта, порожній стан, помилка, пошук/показник з адреси, шторка зі
 * значеннями з `GET /permit-lists/{key}`, додавання/перейменування/прибирання значень (`PUT …/items`) лише з правом.
 *
 * Мутаційний доказ (вручну, відновлено): без `row.editable &&` перед формою додавання «без права» показує поле й кнопки
 * (падає «без права лише читання»); `onRowClick` без `panel` не відкриває шторку; `Update` з порожнім `id` валить
 * «перейменування шле Update з id».
 */
vi.mock('@/shared/i18n', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/i18n')>()),
  t: (key: string, params?: Record<string, string | number>) =>
    params === undefined ? key : `${key} ${JSON.stringify(params)}`,
  language: () => 'en',
}));
vi.mock('@/shared/session/useSession', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/session/useSession')>()),
  useSession: () => ({ data: { permissions: ['Registry.View', 'Permit.EditType'], isSimulation: false } }),
}));

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

const use = (type: string, table: string, field: string, typeName: string) => ({
  typeCode: type,
  typeNameL10n: { en: typeName },
  tableCode: table,
  tableNameL10n: { en: table },
  fieldCode: field,
  fieldNameL10n: { en: field },
});

const SharedKey = 'shared:SUBSTANCES';
const TypeKey = 'type:Air:Emission:Method';

function lists(editable: boolean): PermitListDto[] {
  return [
    {
      key: SharedKey,
      scope: 'SharedRegistry',
      registryCode: 'SUBSTANCES',
      typeCode: null,
      nameL10n: { en: 'Substances' },
      itemCount: 42,
      editable,
      usedBy: [use('Air', 'Emission', 'Substance', 'Air permit')],
    },
    {
      key: TypeKey,
      scope: 'TypeOnly',
      registryCode: 'PRM_Air_Emission_Method',
      typeCode: 'Air',
      nameL10n: { en: 'Method' },
      itemCount: 2,
      editable,
      usedBy: [use('Air', 'Emission', 'Method', 'Air permit')],
    },
  ];
}

const value = (id: number, text: string): PermitListValue => ({ id, code: `V${String(id)}`, displayL10n: { en: text } });

interface Backend {
  editable: boolean;
  listStatus: number;
  detailStatus: number;
  items: Record<string, PermitListValue[]>;
}

interface Calls {
  requests: string[];
  puts: { url: string; body: unknown }[];
}

function stub(over: Partial<Backend> = {}): Calls {
  const backend: Backend = {
    editable: true,
    listStatus: 200,
    detailStatus: 200,
    items: {
      [SharedKey]: [value(1, 'Nitrogen oxides')],
      [TypeKey]: [value(10, 'Direct'), value(11, 'Calc')],
    },
    ...over,
  };
  const calls: Calls = { requests: [], puts: [] };
  const keyOf = (url: string): string | undefined => {
    const match = /\/api\/v1\/permit-lists\/([^/?]+)/.exec(url);

    return match?.[1] === undefined ? undefined : decodeURIComponent(match[1]);
  };
  const detail = (key: string): PermitListDetail => ({
    list: lists(backend.editable).find((list) => list.key === key) as PermitListDto,
    items: backend.items[key] ?? [],
  });

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      calls.requests.push(url);
      const key = keyOf(url);
      if (key === undefined) {
        return backend.listStatus === 200 ? json(lists(backend.editable)) : json({ title: 'failure' }, backend.listStatus);
      }
      if (init?.method === 'PUT') {
        const body = JSON.parse(String(init.body)) as { changes: { action: string; id: number | null; displayL10n: { en?: string } | null }[] };
        calls.puts.push({ url, body });
        let items = backend.items[key] ?? [];
        for (const change of body.changes) {
          if (change.action === 'Add') items = [...items, value(100 + items.length, change.displayL10n?.en ?? '')];
          else if (change.action === 'Update') items = items.map((item) => (item.id === change.id ? value(item.id, change.displayL10n?.en ?? '') : item));
          else items = items.filter((item) => item.id !== change.id);
        }
        backend.items[key] = items;

        return json(detail(key));
      }
      if (backend.detailStatus !== 200) return json({ title: 'forbidden' }, backend.detailStatus);

      return json(detail(key));
    }),
  );

  return calls;
}

function Where(): null {
  (globalThis as { __where?: string }).__where = useLocation().search;

  return null;
}

function show(entry = '/admin/permits/lists'): void {
  render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter initialEntries={[entry]}>
        <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
          <Where />
          <Routes>
            <Route path="/admin/permits/lists" element={<PermitListsPage />} />
          </Routes>
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  delete (globalThis as { __where?: string }).__where;
});

describe('Lists: перелік', () => {
  it('показує списки з одного запиту; з правом - підказка про редагування', async () => {
    const calls = stub();
    show();

    expect(await screen.findByText('Substances')).toBeTruthy();
    expect(screen.getByText('Method')).toBeTruthy();
    expect(screen.getByText('42')).toBeTruthy();
    expect(screen.getByText('permits.lists.editable')).toBeTruthy();
    expect(screen.queryByText('permits.lists.readOnly')).toBeNull();
    expect(calls.requests).toEqual(['/api/v1/permit-lists']);
  });

  it('без права редагувати - підказка «лише читання»', async () => {
    stub({ editable: false });
    show();

    expect(await screen.findByText('permits.lists.readOnly')).toBeTruthy();
    expect(screen.queryByText('permits.lists.editable')).toBeNull();
  });

  it('жодного списку - порожній стан без показників', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => json([])));
    show();

    expect(await screen.findByText('permits.lists.empty.title')).toBeTruthy();
    expect(screen.queryByLabelText('permits.lists.stats.aria')).toBeNull();
  });

  it('збій переліку - помилка з повтором', async () => {
    stub({ listStatus: 500 });
    show();

    expect((await screen.findAllByRole('alert')).length).toBeGreaterThan(0);
  });

  it('невідомі ?kind=, ?stat=, ?panel= ігноруються: усі списки, шторки немає', async () => {
    stub();
    show('/admin/permits/lists?kind=bogus&stat=nope&panel=nothing');

    expect(await screen.findByText('Substances')).toBeTruthy();
    expect(screen.getByText('Method')).toBeTruthy();
    expect(screen.queryByRole('dialog')).toBeNull();
  });

  it('«Used by» словами: «1 column» у таблиці й у шторці', async () => {
    stub();
    show();

    expect((await screen.findAllByText('permits.lists.used.one')).length).toBeGreaterThan(0);
    fireEvent.click(screen.getByText('Method'));
    expect(within(await screen.findByRole('dialog')).getByText('permits.lists.used.one')).toBeTruthy();
  });

  it('показник у адресі лишає лише обране походження', async () => {
    stub();
    show('/admin/permits/lists?stat=type');

    expect(await screen.findByText('Method')).toBeTruthy();
    expect(screen.queryByText('Substances')).toBeNull();
  });

  it('пошук без збігів - окремий текст', async () => {
    stub();
    show('/admin/permits/lists?q=zzz');

    expect(await screen.findByText('permits.lists.noMatches')).toBeTruthy();
  });
});

describe('Lists: шторка', () => {
  it('спільний список: значення з ендпоінта списку та вхід у Registries', async () => {
    stub();
    show();

    fireEvent.click(await screen.findByText('Substances'));

    await waitFor(() => expect((globalThis as { __where?: string }).__where).toContain('panel=shared'));
    const drawer = await screen.findByRole('dialog');
    expect(await within(drawer).findByText('Nitrogen oxides')).toBeTruthy();
    expect(within(drawer).getByText('permits.lists.drawer.note.shared')).toBeTruthy();
    expect(drawer.querySelector('[data-list-open="registry"]')?.getAttribute('href')).toBe('/admin/registries/SUBSTANCES/entries');
  });

  it('«лише тип»: значення з ендпоінта списку, вхід у конструктор типу, примітка про зміну', async () => {
    const calls = stub();
    show();

    fireEvent.click(await screen.findByText('Method'));

    const drawer = await screen.findByRole('dialog');
    expect(await within(drawer).findByText('Direct')).toBeTruthy();
    expect(within(drawer).getByText('Calc')).toBeTruthy();
    expect(within(drawer).getByText(/permits\.lists\.drawer\.note\.typeEditable/)).toBeTruthy();
    expect(drawer.querySelector('[data-list-open="type"]')?.getAttribute('href')).toBe('/admin/permits/types/Air');
    expect(calls.requests).toContain(`/api/v1/permit-lists/${encodeURIComponent(TypeKey)}`);
    expect(calls.requests.some((url) => url.includes('/entries') || url.includes('/permit-types'))).toBe(false);
  });

  it('403 на значення: шторка показує помилку, екран живий', async () => {
    stub({ detailStatus: 403 });
    show();

    fireEvent.click(await screen.findByText('Substances'));
    const drawer = await screen.findByRole('dialog');

    expect(await within(drawer).findByRole('alert')).toBeTruthy();
    expect(within(drawer).getByText('permits.lists.drawer.note.shared')).toBeTruthy();
    expect(screen.getByText('Method')).toBeTruthy();
  });

  it('без права лише читання: ні поля додавання, ні Rename/Remove', async () => {
    stub({ editable: false });
    show();

    fireEvent.click(await screen.findByText('Method'));
    const drawer = await screen.findByRole('dialog');

    expect(await within(drawer).findByText('Direct')).toBeTruthy();
    expect(within(drawer).queryByRole('textbox')).toBeNull();
    expect(within(drawer).queryByText('permits.lists.drawer.rename')).toBeNull();
    expect(within(drawer).queryByText('permits.lists.drawer.remove')).toBeNull();
    expect(within(drawer).getByText(/permits\.lists\.drawer\.note\.type /)).toBeTruthy();
  });
});

describe('Lists: правка значень', () => {
  const itemsUrl = `/api/v1/permit-lists/${encodeURIComponent(TypeKey)}/items`;

  async function openType(): Promise<HTMLElement> {
    fireEvent.click(await screen.findByText('Method'));
    const drawer = await screen.findByRole('dialog');
    await within(drawer).findByText('Direct');

    return drawer;
  }

  it('додавання: PUT з Add і підписом en, нове значення видно в шторці', async () => {
    const calls = stub();
    show();
    const drawer = await openType();

    fireEvent.change(within(drawer).getByRole('textbox', { name: 'permits.lists.drawer.add.label' }), { target: { value: ' Manual ' } });
    fireEvent.click(within(drawer).getByRole('button', { name: 'permits.lists.drawer.add.submit' }));

    expect(await within(drawer).findByText('Manual')).toBeTruthy();
    expect(calls.puts).toEqual([{ url: itemsUrl, body: { changes: [{ action: 'Add', id: null, displayL10n: { en: 'Manual' } }] } }]);
    // Поле очищено, перелік перечитано (лічильники).
    expect(within(drawer).getByRole('textbox', { name: 'permits.lists.drawer.add.label' })).toHaveProperty('value', '');
    await waitFor(() => expect(calls.requests.filter((url) => url === '/api/v1/permit-lists').length).toBeGreaterThan(1));
  });

  it('порожнє значення не відправляється: кнопка додавання вимкнена', async () => {
    const calls = stub();
    show();
    const drawer = await openType();

    expect(within(drawer).getByRole('button', { name: 'permits.lists.drawer.add.submit' })).toHaveProperty('disabled', true);
    fireEvent.change(within(drawer).getByRole('textbox', { name: 'permits.lists.drawer.add.label' }), { target: { value: '   ' } });
    expect(within(drawer).getByRole('button', { name: 'permits.lists.drawer.add.submit' })).toHaveProperty('disabled', true);
    expect(calls.puts).toHaveLength(0);
  });

  it('перейменування шле Update з id і новим підписом', async () => {
    const calls = stub();
    show();
    const drawer = await openType();

    fireEvent.click(within(drawer).getByRole('button', { name: 'permits.lists.drawer.renameAria {"value":"Calc"}' }));
    const input = within(drawer).getByRole('textbox', { name: 'permits.lists.drawer.edit.label' });
    expect(input).toHaveProperty('value', 'Calc');
    fireEvent.change(input, { target: { value: 'Calculation' } });
    fireEvent.click(within(drawer).getByRole('button', { name: 'permits.lists.drawer.edit.save' }));

    expect(await within(drawer).findByText('Calculation')).toBeTruthy();
    expect(calls.puts).toEqual([{ url: itemsUrl, body: { changes: [{ action: 'Update', id: 11, displayL10n: { en: 'Calculation' } }] } }]);
    expect(within(drawer).queryByText('Calc')).toBeNull();
  });

  it('прибирання шле Remove з id, значення зникає', async () => {
    const calls = stub();
    show();
    const drawer = await openType();

    fireEvent.click(within(drawer).getByRole('button', { name: 'permits.lists.drawer.removeAria {"value":"Direct"}' }));

    await waitFor(() => expect(within(drawer).queryByText('Direct')).toBeNull());
    expect(calls.puts).toEqual([{ url: itemsUrl, body: { changes: [{ action: 'Remove', id: 10, displayL10n: null }] } }]);
    expect(within(drawer).getByText('Calc')).toBeTruthy();
  });
});

describe('Lists: фокус у шторці (PA4-U-01)', () => {
  async function openType(): Promise<HTMLElement> {
    fireEvent.click(await screen.findByText('Method'));
    const drawer = await screen.findByRole('dialog');
    await within(drawer).findByText('Direct');

    return drawer;
  }
  const renameOf = (drawer: HTMLElement, name: string): HTMLElement =>
    within(drawer).getByRole('button', { name: `permits.lists.drawer.renameAria {"value":"${name}"}` });

  it('Rename -> фокус у полі; Cancel -> фокус на Rename цього значення', async () => {
    stub();
    show();
    const drawer = await openType();

    fireEvent.click(renameOf(drawer, 'Calc'));
    const input = within(drawer).getByRole('textbox', { name: 'permits.lists.drawer.edit.label' });
    await waitFor(() => expect(document.activeElement).toBe(input));

    fireEvent.click(within(drawer).getByRole('button', { name: 'permits.lists.drawer.edit.cancel' }));
    await waitFor(() => expect(document.activeElement).toBe(renameOf(drawer, 'Calc')));
  });

  it('Save -> фокус на Rename перейменованого значення', async () => {
    stub();
    show();
    const drawer = await openType();

    fireEvent.click(renameOf(drawer, 'Calc'));
    fireEvent.change(within(drawer).getByRole('textbox', { name: 'permits.lists.drawer.edit.label' }), { target: { value: 'Calculation' } });
    fireEvent.click(within(drawer).getByRole('button', { name: 'permits.lists.drawer.edit.save' }));

    await within(drawer).findByText('Calculation');
    await waitFor(() => expect(document.activeElement).toBe(renameOf(drawer, 'Calculation')));
  });

  it('Remove -> фокус на Rename сусіднього значення, а для останнього - на поле додавання', async () => {
    stub();
    show();
    const drawer = await openType();

    fireEvent.click(within(drawer).getByRole('button', { name: 'permits.lists.drawer.removeAria {"value":"Direct"}' }));
    await waitFor(() => expect(within(drawer).queryByText('Direct')).toBeNull());
    await waitFor(() => expect(document.activeElement).toBe(renameOf(drawer, 'Calc')));

    fireEvent.click(within(drawer).getByRole('button', { name: 'permits.lists.drawer.removeAria {"value":"Calc"}' }));
    await waitFor(() => expect(within(drawer).queryByText('Calc')).toBeNull());
    await waitFor(() => expect(document.activeElement).toBe(within(drawer).getByRole('textbox', { name: 'permits.lists.drawer.add.label' })));
  });

  it('Add -> фокус на полі додавання (кнопка стала вимкненою - фокус не падає на BODY)', async () => {
    stub();
    show();
    const drawer = await openType();
    const field = within(drawer).getByRole('textbox', { name: 'permits.lists.drawer.add.label' });

    fireEvent.change(field, { target: { value: 'Manual' } });
    fireEvent.click(within(drawer).getByRole('button', { name: 'permits.lists.drawer.add.submit' }));

    await within(drawer).findByText('Manual');
    await waitFor(() => expect(document.activeElement).toBe(field));
  });

  it('шторка при відкритті забирає фокус на себе', async () => {
    stub();
    show();
    const drawer = await openType();

    await waitFor(() => expect(drawer.contains(document.activeElement)).toBe(true));
  });
});

describe('Lists: довге значення (PA4-U-03)', () => {
  it('значення без пробілів несе клас переносу (самі стилі перевіряє e2e у справжньому браузері)', async () => {
    const long = 'Z'.repeat(240);
    stub({ items: { [TypeKey]: [value(10, long)] } });
    show();
    fireEvent.click(await screen.findByText('Method'));
    const drawer = await screen.findByRole('dialog');

    const text = await within(drawer).findByText(long);
    expect(text.className).toContain('pl-value-text');
  });
});