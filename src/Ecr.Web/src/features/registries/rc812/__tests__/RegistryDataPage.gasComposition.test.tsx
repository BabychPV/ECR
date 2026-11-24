import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, configure, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { loadCatalog } from '@/shared/i18n';
import { MeQueryKey } from '@/shared/session/useSession';
import { Catalog } from '@/test/__tests__/a11yFixtures';
import { testTheme } from '@/test/render';
import { RegistryDataPage } from '../RegistryDataPage';

/**
 * D-NEW (людина 24.11): довідники складу газу (`STREAM`, `STREAM_CASE`, `GAS_COMPOSITION`) пише лише сторінка «Склад газу».
 * Загальний редактор даних довідника показує їх лише для читання, зі смугою «керується сторінкою Склад газу» й посиланням
 * туди. До імпорту з FLERT чи першого потоку довідника немає — нейтральний стан замість червоного «не знайдено» і без
 * «New entry» (P3 F13 #841, D-476 AU F13 #827 — правило лишилось, змінився лише власник).
 *
 * ⛔ Доказ: прибрати ``gasCode`` з ``readOnly`` у ``RegistryDataPage.tsx`` — тест «наявний довідник складу газу лише для
 * читання» червоний (є «Save»); прибрати ``gasMissing`` — тести відсутніх довідників червоні (``alert`` і «New entry»).
 * Контроль: довідник НЕ складу газу, якого немає, і далі показує помилку з кодом і «New entry».
 */
configure({ asyncUtilTimeout: 10_000 });

const Managed = Catalog['gasComposition.registryManaged'];
const Open = Catalog['gasComposition.open'];
const NewEntry = Catalog['registries.newEntry'];

const json = (body: unknown, status = 200): Response =>
  new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json' },
  });

const notFound = (code: string): Response =>
  json(
    {
      title: 'Registry item not found',
      status: 404,
      errorCode: 'ECR-REG-0404',
      correlationId: 'corr-gas',
      detail: `Registry "${code}" was not found.`,
    },
    404,
  );

/** Перелік довідників без складу газу; опис і рядки будь-якого довідника — 404, як на стенді до імпорту. */
function serve(
  permissions: readonly string[] = ['Registry.View', 'Registry.EditData', 'Registry.EditDefinition', 'GasComposition.View'],
): ReturnType<typeof vi.fn> {
  const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
    const url = String(input);
    if (url.includes('/ui-strings/')) return json({ languageCode: 'en', revision: 1, strings: Catalog });
    if (url.includes('/api/v1/me')) {
      return json({
        denies: [],
        grants: {},
        isSimulation: false,
        language: 'en',
        mustChangePassword: false,
        permissions,
        simulatedForUserId: null,
        userId: 1,
        userName: 'tester',
      });
    }
    const missing = /\/api\/v1\/registries\/([^/?]+)\//.exec(url);
    if (missing !== null) return notFound(decodeURIComponent(missing[1]!));
    if (url.endsWith('/api/v1/registries')) {
      return json([
        {
          id: 68,
          code: 'AIR_AREA',
          nameL10n: { values: { en: 'Air permit areas' } },
          fields: [],
          isTemporal: false,
          isHierarchical: false,
          sourceKind: 'Local',
        },
      ]);
    }
    return json(null);
  });
  vi.stubGlobal('fetch', fetchMock);
  return fetchMock;
}

/** Шляхи всіх запитів сторінки до цього моменту. */
const requested = (fetchMock: ReturnType<typeof vi.fn>): string[] => fetchMock.mock.calls.map((call) => String(call[0]));

function show(code: string): QueryClient {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={[`/admin/registries/${code}/entries`]}>
          <Routes>
            <Route path="/admin/registries/:code/entries" element={<RegistryDataPage />} />
          </Routes>
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
  return client;
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('D-NEW: довідники складу газу керуються сторінкою «Склад газу»', () => {
  it('сід має текст смуги й посилання англійською', () => {
    expect(Managed).toBe('These entries are the gas composition: they are maintained on the Gas composition page.');
    expect(Open).toBe('Open gas composition');
  });

  it.each(['STREAM', 'STREAM_CASE', 'GAS_COMPOSITION'])(
    '%s до імпорту: нейтральна смуга замість помилки, без «New entry»',
    async (code) => {
      serve();
      await loadCatalog('en', 'private');
      const client = show(code);

      expect(await screen.findByText(Managed!)).toBeTruthy();
      // ⚠ Профіль із `Registry.EditData` має ПРИЇХАТИ: до нього `can()` дає `false`, і «немає кнопки» було б
      // зеленим на будь-якому коді.
      await waitFor(() => expect(client.getQueryData(MeQueryKey)).toBeDefined());
      await act(async () => {
        await Promise.resolve();
      });
      expect(screen.queryByRole('alert')).toBeNull();
      expect(screen.queryByText(/was not found/)).toBeNull();
      expect(screen.queryByRole('button', { name: NewEntry! })).toBeNull();
    },
  );

  it('контроль: відсутній довідник поза складом газу — і далі помилка з кодом і «New entry»', async () => {
    serve();
    await loadCatalog('en', 'private');
    show('NO_SUCH');

    expect((await screen.findByRole('alert')).textContent).toContain('ECR-REG-0404');
    expect(screen.queryByText(Managed!)).toBeNull();
    expect(await screen.findByRole('button', { name: NewEntry! })).toBeTruthy();
  });

  /** P3 F13 #841 (a): ⛔ Доказ — повернути `useRegistryDefinition(code)` без `enabled` у `RegistryDataPage.tsx` — червоний. */
  it('(a) опис довідника, якого немає в переліку, не запитується — жодного 404', async () => {
    const fetchMock = serve();
    await loadCatalog('en', 'private');
    show('STREAM');

    expect(await screen.findByText(Managed!)).toBeTruthy();
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 50));
    });
    expect(requested(fetchMock).filter((url) => url.includes('/api/v1/registries/STREAM'))).toEqual([]);
  });

  it('(b) шапка називає код один раз і без лічильника 0', async () => {
    serve();
    await loadCatalog('en', 'private');
    show('STREAM');

    expect(await screen.findByText(Managed!)).toBeTruthy();
    expect(screen.getAllByText('STREAM')).toHaveLength(1);
    expect(screen.queryByText('0')).toBeNull();
  });

  it('з правом перегляду складу газу — посилання на сторінку «Склад газу»', async () => {
    serve();
    await loadCatalog('en', 'private');
    const client = show('STREAM_CASE');

    await waitFor(() => expect(client.getQueryData(MeQueryKey)).toBeDefined());
    const link = await screen.findByRole('link', { name: Open! });
    expect(link.getAttribute('href')).toBe('/admin/gas-composition');
    expect(screen.getByTestId('gas-composition-managed').contains(link)).toBe(true);
  });

  it('без права перегляду складу газу — посилання немає', async () => {
    serve(['Registry.View']);
    await loadCatalog('en', 'private');
    const client = show('STREAM_CASE');

    expect(await screen.findByText(Managed!)).toBeTruthy();
    await waitFor(() => expect(client.getQueryData(MeQueryKey)).toBeDefined());
    await act(async () => {
      await Promise.resolve();
    });
    expect(screen.queryByRole('link', { name: Open! })).toBeNull();
  });
});
