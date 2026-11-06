import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { apiFetch } from '@/api/client';
import { PermitPage } from '@/pages/admin/PermitPage';
import { Catalog } from '@/test/__tests__/a11yFixtures';
import { TestProviders } from '@/test/render';
import * as airApi from '@/features/permits/air/airApi';
import * as permitsApi from '@/features/permits/api/permitsApi';
import * as typesApi from '@/features/permits/api/typesApi';
import { permit, permitType } from '../../permit/__tests__/permitFixtures';
import { card } from './airFixtures';

/**
 * Вкладка «Sources & parameters» на екрані дозволу: є лише в дозволу типу `AIR` (AIR-ON-AIR); адреса `?tab=air` її відкриває;
 * інший тип з `?tab=air` падає на Overview (вкладки там немає).
 *
 * Мутаційний доказ: умова `hasAir` замінена на `true` валить «звичайний тип».
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
vi.mock('@/features/permits/api/permitsApi', () => ({ getPermit: vi.fn() }));
vi.mock('@/features/permits/api/typesApi', () => ({ getPermitType: vi.fn() }));
vi.mock('@/features/permits/air/airApi', () => ({ getAirLimits: vi.fn() }));
// The table tab is a lazy chunk the page preloads when the permit has tables: a stub keeps it out of these tests (they look
// only at which tabs exist), otherwise the import resolves after the test and Vite reports «module runner has been closed».
vi.mock('@/features/permits/permit/TableTab', () => ({ TableTab: () => null }));
const session = vi.hoisted(() => ({ permissions: ['Registry.View', 'Registry.EditData'] as string[] }));
vi.mock('@/shared/session/useSession', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/session/useSession')>()),
  useSession: () => ({ data: { permissions: session.permissions, isSimulation: false } }),
}));

beforeEach(() => {
  vi.mocked(apiFetch).mockResolvedValue([] as never);
  vi.mocked(typesApi.getPermitType).mockResolvedValue(permitType());
  vi.mocked(airApi.getAirLimits).mockResolvedValue(card());
});
afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

type PermitTables = Parameters<typeof permit>[0] extends infer P ? (P extends { tables?: infer T } ? T : never) : never;

/** The AIR type's two import tables plus its own EMISSION_LIMITS, as the server lists them on the permit. */
const AirTables = [
  { code: 'AIR_SOURCES', nameL10n: { en: 'Sources (NAE)' }, registryCode: 'PRM_AIR_AIR_SOURCES', rowCount: 373, total: null, totalColumnCode: null },
  { code: 'AIR_SOURCE_SUBSTANCES', nameL10n: { en: 'Substances of sources' }, registryCode: 'PRM_AIR_AIR_SOURCE_SUBSTANCES', rowCount: 2624, total: null, totalColumnCode: null },
  { code: 'EMISSION_LIMITS', nameL10n: { en: 'Emission limits' }, registryCode: 'PRM_AIR_EMISSION_LIMITS', rowCount: 0, total: null, totalColumnCode: null },
] as unknown as PermitTables;

async function open(url: string, typeCode: string, status?: 'Expired', tables: PermitTables = [] as unknown as PermitTables): Promise<void> {
  vi.mocked(permitsApi.getPermit).mockResolvedValue(permit({ typeCode, tables, ...(status === undefined ? {} : { status }) }));
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <TestProviders>
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={[url]}>
          <Routes>
            <Route path="/admin/permits/:id" element={<PermitPage />} />
          </Routes>
        </MemoryRouter>
      </QueryClientProvider>
    </TestProviders>,
  );
  await screen.findByRole('heading', { level: 1, name: 'KZ-100' });
}

describe('екран дозволу AIR: вкладка «Sources & parameters» і «Import parameters…»', () => {
  it('дозвіл AIR: вкладка є, ?tab=air відкриває картку; «Import parameters…» у вкладці і в шапці дозволу', async () => {
    await open('/admin/permits/5?tab=air', 'AIR');
    expect(screen.getByRole('tab', { name: Catalog['permits.page.tabs.air'] ?? '' }).getAttribute('aria-selected')).toBe('true');
    await screen.findByText('Source 1085');
    const links = screen.getAllByRole('link', { name: Catalog['permits.air.import'] ?? '' });
    expect(links).toHaveLength(2);
    expect(links.every((link) => link.getAttribute('href') === '/admin/permits/5/import')).toBe(true);
  });

  it('кнопка в шапці є й на Overview (поруч із Renew/More)', async () => {
    await open('/admin/permits/5', 'AIR');
    expect(screen.getByRole('link', { name: Catalog['permits.air.import'] ?? '' }).getAttribute('href')).toBe('/admin/permits/5/import');
  });

  it('закінчений дозвіл: картка читається, кнопки імпорту немає', async () => {
    await open('/admin/permits/5?tab=air', 'AIR', 'Expired');
    await screen.findByText('Source 1085');
    expect(screen.queryByRole('link', { name: Catalog['permits.air.import'] ?? '' })).toBeNull();
  });

  it('P1 07.11: у дозволу AIR немає вкладок «Sources (NAE)» і «Substances of sources» (їх показує «Sources & parameters»); власна таблиця типу лишається', async () => {
    await open('/admin/permits/5?tab=AIR_SOURCES', 'AIR', undefined, AirTables);

    expect(screen.queryByRole('tab', { name: /Sources \(NAE\)/ })).toBeNull();
    expect(screen.queryByRole('tab', { name: /Substances of sources/ })).toBeNull();
    expect(screen.getByRole('tab', { name: /Emission limits/ })).not.toBeNull();
    // Адреса прихованої вкладки падає на Overview, а не відкриває сиру таблицю.
    expect(screen.getByRole('tab', { name: Catalog['permits.page.tabs.overview'] ?? '' }).getAttribute('aria-selected')).toBe('true');
  });

  it('інший тип із таблицями тих самих кодів — вкладки на місці', async () => {
    await open('/admin/permits/5', 'WaterDischarge', undefined, AirTables);

    expect(screen.getByRole('tab', { name: /Sources \(NAE\)/ })).not.toBeNull();
    expect(screen.getByRole('tab', { name: /Substances of sources/ })).not.toBeNull();
  });

  it('звичайний тип: вкладки немає, ?tab=air падає на Overview', async () => {
    await open('/admin/permits/5?tab=air', 'WaterDischarge');
    expect(screen.queryByRole('tab', { name: Catalog['permits.page.tabs.air'] ?? '' })).toBeNull();
    expect(screen.getByRole('tab', { name: Catalog['permits.page.tabs.overview'] ?? '' }).getAttribute('aria-selected')).toBe('true');
    expect(airApi.getAirLimits).not.toHaveBeenCalled();
  });
});
