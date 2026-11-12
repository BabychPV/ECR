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

/** Перелік довідників без дзеркал FLERT; опис і рядки будь-якого довідника — 404, як на стенді до першого синку. */
function serve(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('/ui-strings/')) return json({ languageCode: 'en', revision: 1, strings: Catalog });
      if (url.includes('/api/v1/me')) {
        return json({
          denies: [],
          grants: {},
          isSimulation: false,
          language: 'en',
          mustChangePassword: false,
          permissions: ['Registry.View', 'Registry.EditData', 'Registry.EditDefinition'],
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
    }),
  );
}

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
});
