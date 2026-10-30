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
 * Вкладка «Air limits» на екрані дозволу: є лише в дозволу типу `AIR_LIMITS`; адреса `?tab=air` її відкриває;
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

async function open(url: string, typeCode: string, status?: 'Expired'): Promise<void> {
  vi.mocked(permitsApi.getPermit).mockResolvedValue(permit({ typeCode, tables: [], ...(status === undefined ? {} : { status }) }));
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

describe('екран дозволу: вкладка «Air limits»', () => {
  it('дозвіл AIR_LIMITS: вкладка є, ?tab=air відкриває картку з кнопкою імпорту', async () => {
    await open('/admin/permits/5?tab=air', 'AIR_LIMITS');
    expect(screen.getByRole('tab', { name: Catalog['permits.page.tabs.air'] ?? '' }).getAttribute('aria-selected')).toBe('true');
    await screen.findByText('Source 1085');
    expect(screen.getByRole('link', { name: Catalog['permits.air.import'] ?? '' })).toBeTruthy();
  });

  it('закінчений дозвіл: картка читається, кнопки імпорту немає', async () => {
    await open('/admin/permits/5?tab=air', 'AIR_LIMITS', 'Expired');
    await screen.findByText('Source 1085');
    expect(screen.queryByRole('link', { name: Catalog['permits.air.import'] ?? '' })).toBeNull();
  });

  it('звичайний тип: вкладки немає, ?tab=air падає на Overview', async () => {
    await open('/admin/permits/5?tab=air', 'WaterDischarge');
    expect(screen.queryByRole('tab', { name: Catalog['permits.page.tabs.air'] ?? '' })).toBeNull();
    expect(screen.getByRole('tab', { name: Catalog['permits.page.tabs.overview'] ?? '' }).getAttribute('aria-selected')).toBe('true');
    expect(airApi.getAirLimits).not.toHaveBeenCalled();
  });
});
