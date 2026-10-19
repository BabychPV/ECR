import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom';
import { testTheme } from '@/test/render';
import { PermitListsPage } from '@/pages/admin/PermitListsPage';
import { column, permitType, table } from '../../typeEditor/__tests__/fixtures';
import { makeType } from '../../types/__tests__/typesFixture';

/**
 * Екран «Lists» (PM-08d): дані з типів і довідників, порожній стан, помилка, пошук/показник з адреси, шторка
 * (значення спільного довідника, «лише тип» read-only, дії), відмова довідників не валить екран.
 *
 * Мутаційний доказ (вручну, відновлено): без `hasLists &&` порожній стан малював би нулі; `onRowClick` без
 * `panel` не відкриває шторку; `registriesFailed` без Alert ховає причину відсутніх кількостей.
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

const air = permitType({
  code: 'Air',
  nameL10n: { en: 'Air permit' },
  headerFields: [],
  tables: [
    table('Emission', [
      column('Substance', { dataType: 'Lookup', listScope: 'SharedRegistry', listRegistryCode: 'SUBSTANCES' }),
      column('Method', { dataType: 'Lookup', listScope: 'TypeOnly', choices: ['Direct', 'Calc'] }),
    ]),
  ],
});

interface Backend {
  types: ReturnType<typeof makeType>[];
  registriesStatus: number;
  typeStatus: number;
  entriesStatus: number;
}

function stub(over: Partial<Backend> = {}): string[] {
  const backend: Backend = { types: [makeType('Air')], registriesStatus: 200, typeStatus: 200, entriesStatus: 200, ...over };
  const requests: string[] = [];
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      requests.push(url);
      if (url.includes('/entries') && backend.entriesStatus !== 200) return json({ title: 'forbidden' }, backend.entriesStatus);
      if (url.includes('/entries')) return json([{ id: 1, code: 'NOX', display: 'Nitrogen oxides', parentEntryId: null, validFrom: null, validTo: null }]);
      if (url.includes('/api/v1/registries')) {
        return backend.registriesStatus === 200
          ? json([{ code: 'SUBSTANCES', nameL10n: { values: { en: 'Substances' } }, entryCount: 42, fields: [] }])
          : json({ title: 'failure' }, backend.registriesStatus);
      }
      if (url.includes('/api/v1/permit-types/')) {
        return backend.typeStatus === 200 ? json(air) : json({ title: 'failure' }, backend.typeStatus);
      }

      return json({ items: backend.types, nextCursor: null, totalCount: backend.types.length });
    }),
  );

  return requests;
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

describe('Lists (PM-08d)', () => {
  it('показує списки з типів і довідників, а також пояснення «лише читання»', async () => {
    stub();
    show();

    expect(await screen.findByText('Substances')).toBeTruthy();
    expect(screen.getByText('Method')).toBeTruthy();
    expect(screen.getByText('permits.lists.readOnly')).toBeTruthy();
    expect(screen.getByText('42')).toBeTruthy();
  });

  it('жодного типу — порожній стан без показників', async () => {
    stub({ types: [] });
    show();

    expect(await screen.findByText('permits.lists.empty.title')).toBeTruthy();
    expect(screen.queryByLabelText('permits.lists.stats.aria')).toBeNull();
  });

  it('збій читання типу не валить екран: є попередження з кількістю', async () => {
    stub({ typeStatus: 500 });
    show();

    await waitFor(() => expect(document.querySelector('[data-lists-types-failed]')).not.toBeNull());
    expect(document.querySelector('[data-lists-types-failed]')?.textContent).toContain('"count":1');
    expect(screen.getByText('permits.lists.readOnly')).toBeTruthy();
  });

  it('збій переліку типів — помилка з повтором', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => json({ title: 'failure' }, 500)));
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

  it('403 на значення довідника: шторка показує помилку, екран живий', async () => {
    stub({ entriesStatus: 403 });
    show();

    fireEvent.click(await screen.findByText('Substances'));
    const drawer = await screen.findByRole('dialog');

    expect(await within(drawer).findByRole('alert')).toBeTruthy();
    expect(within(drawer).getByText('permits.lists.drawer.note.shared')).toBeTruthy();
    expect(screen.getByText('Method')).toBeTruthy();
  });

  it('«Used by» словами: «1 column» у таблиці й у шторці', async () => {
    stub();
    show();

    expect((await screen.findAllByText('permits.lists.used.one')).length).toBeGreaterThan(0);
    fireEvent.click(screen.getByText('Method'));
    expect(within(await screen.findByRole('dialog')).getByText('permits.lists.used.one')).toBeTruthy();
  });

  it('довідники недоступні — екран живий, назва — код, є пояснення', async () => {
    stub({ registriesStatus: 403 });
    show();

    expect(await screen.findByText('SUBSTANCES')).toBeTruthy();
    expect(document.querySelector('[data-lists-registries-failed]')).not.toBeNull();
  });

  it('показник у адресі лишає лише обране походження', async () => {
    stub();
    show('/admin/permits/lists?stat=type');

    expect(await screen.findByText('Method')).toBeTruthy();
    expect(screen.queryByText('Substances')).toBeNull();
  });

  it('пошук без збігів — окремий текст', async () => {
    stub();
    show('/admin/permits/lists?q=zzz');

    expect(await screen.findByText('permits.lists.noMatches')).toBeTruthy();
  });

  it('клік по спільному списку відкриває шторку зі значеннями довідника та входом у Registries', async () => {
    stub();
    show();

    fireEvent.click(await screen.findByText('Substances'));

    await waitFor(() => expect((globalThis as { __where?: string }).__where).toContain('panel=shared'));
    const drawer = await screen.findByRole('dialog');
    expect(await within(drawer).findByText('Nitrogen oxides')).toBeTruthy();
    expect(within(drawer).getByText('permits.lists.drawer.note.shared')).toBeTruthy();
    expect(drawer.querySelector('[data-list-open="registry"]')?.getAttribute('href')).toBe('/admin/registries/SUBSTANCES/entries');
  });

  it('шторка «лише тип»: значення тільки для читання, вхід у конструктор типу', async () => {
    const requests = stub();
    show();

    fireEvent.click(await screen.findByText('Method'));

    const drawer = await screen.findByRole('dialog');
    expect(within(drawer).getByText('Direct')).toBeTruthy();
    expect(within(drawer).getByText('Calc')).toBeTruthy();
    expect(within(drawer).queryByRole('textbox')).toBeNull();
    expect(drawer.querySelector('[data-list-open="type"]')?.getAttribute('href')).toBe('/admin/permits/types/Air');
    // Значення «лише тип» — з визначення типу; довідник не питаємо.
    expect(requests.filter((url) => url.includes('/entries'))).toHaveLength(0);
  });
});
