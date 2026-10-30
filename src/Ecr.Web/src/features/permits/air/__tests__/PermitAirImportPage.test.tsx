import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { PermitAirImportPage } from '@/pages/admin/PermitAirImportPage';
import { Catalog } from '@/test/__tests__/a11yFixtures';
import { TestProviders } from '@/test/render';
import * as airApi from '@/features/permits/air/airApi';
import * as permitsApi from '@/features/permits/api/permitsApi';
import { permit } from '../../permit/__tests__/permitFixtures';
import { profiles } from './airFixtures';

/**
 * Сторінка майстра імпорту (`/admin/permits/:id/import`): тільки для `AIR_LIMITS`; закінчений дозвіл і відсутність
 * права запису дають пояснення, а кнопка «Next» вимкнена; хибний id - «не знайдено».
 *
 * Мутаційний доказ: `supported` замінено на `true` валить «інший тип дозволу».
 */
vi.mock('@/shared/i18n', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/i18n')>()),
  t: (key: string, params?: Record<string, string | number>) =>
    (Catalog[key] ?? `⟦${key}⟧`).replace(/\{(\w+)\}/g, (match, name: string) =>
      params !== undefined && name in params ? String(params[name]) : match,
    ),
}));
vi.mock('@/features/permits/api/permitsApi', () => ({ getPermit: vi.fn() }));
vi.mock('@/features/permits/air/airApi', () => ({ listImportProfiles: vi.fn() }));
const session = vi.hoisted(() => ({ permissions: [] as string[] }));
vi.mock('@/shared/session/useSession', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/session/useSession')>()),
  useSession: () => ({ data: { permissions: session.permissions, isSimulation: false } }),
}));

const text = (key: string): string => Catalog[key] ?? key;

beforeEach(() => {
  session.permissions = ['Registry.View', 'Registry.EditData'];
  vi.mocked(airApi.listImportProfiles).mockResolvedValue(profiles);
});
afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

async function open(url: string, over: Parameters<typeof permit>[0] = { typeCode: 'AIR_LIMITS' }): Promise<void> {
  vi.mocked(permitsApi.getPermit).mockResolvedValue(permit(over));
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <TestProviders>
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={[url]}>
          <Routes>
            <Route path="/admin/permits/:id/import" element={<PermitAirImportPage />} />
          </Routes>
        </MemoryRouter>
      </QueryClientProvider>
    </TestProviders>,
  );
}

describe('сторінка імпорту лімітів', () => {
  it('дозвіл AIR_LIMITS із правом запису: майстер на кроці «Файл», «Next» активна', async () => {
    await open('/admin/permits/5/import');
    expect(await screen.findByRole('heading', { level: 1, name: text('permits.air.import.title') })).toBeTruthy();
    await screen.findByLabelText(new RegExp(text('permits.air.import.region.label')));
    const next = screen.getByRole('button', { name: text('permits.air.import.analyse') }) as HTMLButtonElement;
    expect(next.disabled).toBe(false);
    expect(screen.getByRole('link', { name: /Back to KZ-100/ }).getAttribute('href')).toBe('/admin/permits/5?tab=air');
  });

  it('без права запису: пояснення і вимкнена «Next»', async () => {
    session.permissions = ['Registry.View'];
    await open('/admin/permits/5/import');
    expect(await screen.findByText(text('permits.air.import.readOnly.title'))).toBeTruthy();
    await screen.findByLabelText(new RegExp(text('permits.air.import.region.label')));
    expect((screen.getByRole('button', { name: text('permits.air.import.analyse') }) as HTMLButtonElement).disabled).toBe(true);
  });

  it('закінчений дозвіл: пояснення і вимкнена «Next»', async () => {
    await open('/admin/permits/5/import', { typeCode: 'AIR_LIMITS', status: 'Expired' });
    expect(await screen.findByText(text('permits.air.import.expired.title'))).toBeTruthy();
    await screen.findByLabelText(new RegExp(text('permits.air.import.region.label')));
    expect((screen.getByRole('button', { name: text('permits.air.import.analyse') }) as HTMLButtonElement).disabled).toBe(true);
  });

  it('інший тип дозволу: майстра немає, є пояснення', async () => {
    await open('/admin/permits/5/import', { typeCode: 'WaterDischarge' });
    expect(await screen.findByText(text('permits.air.import.unsupported.title'))).toBeTruthy();
    expect(screen.queryByRole('button', { name: text('permits.air.import.analyse') })).toBeNull();
  });

  it('хибний id - сторінка «не знайдено», запиту немає', async () => {
    await open('/admin/permits/abc/import');
    expect(permitsApi.getPermit).not.toHaveBeenCalled();
  });
});
