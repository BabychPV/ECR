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
 * D-476 (AU F13 #827): дзеркала FLERT (`STREAM`, `STREAM_CASE`, `GAS_COMPOSITION`) заводить перший увімкнений синк.
 * Поки синк вимкнений, довідника немає — сторінка даних показувала червоне «Registry "STREAM" was not found» і
 * «New entry». Тепер — нейтральний текст «з'явиться після першого синку» і жодного створення (master — FLERT).
 *
 * ⛔ Доказ: прибрати `mirrorNotSynced` з `RegistryDataPage.tsx` — тести дзеркал червоні (є `alert` і «New entry»,
 * нейтрального тексту немає). Контроль: довідник НЕ з дзеркала, якого немає, і далі показує помилку з кодом і
 * «New entry» — тобто відсутність кнопки на дзеркалі саме через дзеркало, а не через профіль чи стенд.
 */
configure({ asyncUtilTimeout: 10_000 });

const Pending = Catalog['flert.mirror.pending'];
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
      correlationId: 'corr-flert',
      detail: `Registry "${code}" was not found.`,
    },
    404,
  );

const Designer = Catalog['registries.constructor'];

/** Перелік довідників без дзеркал FLERT; опис і рядки будь-якого довідника — 404, як на стенді до першого синку. */
function serve(
  permissions: readonly string[] = ['Registry.View', 'Registry.EditData', 'Registry.EditDefinition'],
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

describe('D-476: дзеркало FLERT до першого синку', () => {
  it('сід має текст нейтрального стану англійською', () => {
    expect(Pending).toBe('The registry appears after the first FLERT sync.');
  });

  it.each(['STREAM', 'STREAM_CASE', 'GAS_COMPOSITION'])(
    '%s: нейтральний текст замість помилки, без «New entry»',
    async (code) => {
      serve();
      await loadCatalog('en', 'private');
      const client = show(code);

      expect(await screen.findByText(Pending!)).toBeTruthy();
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

  it('контроль: відсутній довідник поза дзеркалом FLERT — і далі помилка з кодом і «New entry»', async () => {
    serve();
    await loadCatalog('en', 'private');
    show('NO_SUCH');

    expect((await screen.findByRole('alert')).textContent).toContain('ECR-REG-0404');
    expect(screen.queryByText(Pending!)).toBeNull();
    expect(await screen.findByRole('button', { name: NewEntry! })).toBeTruthy();
  });

  /**
   * P3 F13 #841 (a): до першого синку сторінка питала опис і отримувала `404` у консолі браузера.
   * ⛔ Доказ: повернути `useRegistryDefinition(code)` без `enabled` у `RegistryDataPage.tsx` — червоний.
   */
  it('(a) опис дзеркала, якого немає в переліку, не запитується — жодного 404', async () => {
    const fetchMock = serve();
    await loadCatalog('en', 'private');
    show('STREAM');

    expect(await screen.findByText(Pending!)).toBeTruthy();
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 50));
    });
    expect(requested(fetchMock).filter((url) => url.includes('/api/v1/registries/STREAM'))).toEqual([]);
  });

  /** ⛔ Доказ (b): повернути незмінні `badge`/`count` у шапці — «STREAM STREAM 0» повертається, тест червоний. */
  it('(b) шапка називає код один раз і без лічильника 0', async () => {
    serve();
    await loadCatalog('en', 'private');
    show('STREAM');

    expect(await screen.findByText(Pending!)).toBeTruthy();
    expect(screen.getAllByText('STREAM')).toHaveLength(1);
    expect(screen.queryByText('0')).toBeNull();
  });

  /** ⛔ Доказ (d): прибрати `Anchor` із нейтральної смуги — перший тест червоний; прибрати `canDesign` — другий. */
  it('(d) з правом на конструктор — посилання «Registry designer» на сторінку довідників', async () => {
    serve();
    await loadCatalog('en', 'private');
    const client = show('STREAM_CASE');

    await waitFor(() => expect(client.getQueryData(MeQueryKey)).toBeDefined());
    const link = await screen.findByRole('link', { name: Designer! });
    expect(link.getAttribute('href')).toBe('/admin/registries');
    expect(screen.getByTestId('flert-mirror-pending').contains(link)).toBe(true);
  });

  it('(d) без права на конструктор — посилання немає', async () => {
    serve(['Registry.View']);
    await loadCatalog('en', 'private');
    const client = show('STREAM_CASE');

    expect(await screen.findByText(Pending!)).toBeTruthy();
    await waitFor(() => expect(client.getQueryData(MeQueryKey)).toBeDefined());
    await act(async () => {
      await Promise.resolve();
    });
    expect(screen.queryByRole('link', { name: Designer! })).toBeNull();
  });
});
