import type { JSX } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { Catalog } from '@/test/__tests__/a11yFixtures';
import { TestProviders } from '@/test/render';
import * as airApi from '@/features/permits/air/airApi';
import { AirLimitsTab } from '../AirLimitsTab';
import { card } from './airFixtures';

/**
 * Вкладка «Air limits» картки дозволу (NAE-план N3). Тексти - справжні, з `09-seed.sql`.
 *
 * Мутаційні докази (виконано вручну, кожну мутацію відновлено):
 * - `AirLimitsTab`: `mayImport &&` прибрано з кнопки імпорту валить «без права запису кнопки імпорту немає»;
 * - `sourceKey`: `naeCode` замість `id` валить «один код НАЕ в двох рядках» (ключі React збігаються, розкриття чіпає обидва);
 * - `useAirLimits`: `structuralSharing: false` не перевіряється поведінкою - це оптимізація, доказ - вимір (звіт).
 */
vi.mock('@/shared/i18n', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/i18n')>()),
  t: (key: string, params?: Record<string, string | number>) =>
    (Catalog[key] ?? `⟦${key}⟧`).replace(/\{(\w+)\}/g, (match, name: string) =>
      params !== undefined && name in params ? String(params[name]) : match,
    ),
}));
vi.mock('@/features/permits/air/airApi', () => ({ getAirLimits: vi.fn() }));

const getAirLimits = vi.mocked(airApi.getAirLimits);
const text = (key: string): string => Catalog[key] ?? key;

beforeEach(() => {
  getAirLimits.mockResolvedValue(card());
});
afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

function mount(mayImport: boolean): JSX.Element {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  return (
    <TestProviders>
      <QueryClientProvider client={client}>
        <MemoryRouter>
          <AirLimitsTab permitId={5} mayImport={mayImport} />
        </MemoryRouter>
      </QueryClientProvider>
    </TestProviders>
  );
}

describe('вкладка «Air limits»', () => {
  it('джерела, лічильники, тип джерела латиницею з сервера - підписом', async () => {
    render(mount(true));
    await screen.findByText('Source 1085');
    expect(screen.getByTestId('air-counts').textContent).toBe('2 sources · 3 substance rows');
    expect(screen.getAllByTestId('air-source-row')).toHaveLength(2);
    const rows = screen.getAllByTestId('air-source-row');
    expect(within(rows[1]!).getByText(text('permits.air.type.diffuseShort'))).toBeTruthy();
    expect(getAirLimits).toHaveBeenCalledWith(5);
  });

  it('розкриття джерела показує речовини з повною точністю; повторний клік згортає', async () => {
    render(mount(true));
    await screen.findByText('Source 1085');
    const toggle = screen.getByRole('button', { name: 'Substances of source 1085' });
    expect(toggle.getAttribute('aria-expanded')).toBe('false');
    fireEvent.click(toggle);
    expect(toggle.getAttribute('aria-expanded')).toBe('true');
    const table = screen.getByRole('table', { name: 'Substances of source 1085' });
    expect(within(table).getByText('0301')).toBeTruthy();
    expect(within(table).getByText('Nitrogen dioxide')).toBeTruthy();
    fireEvent.click(toggle);
    expect(screen.queryByRole('table', { name: 'Substances of source 1085' })).toBeNull();
  });

  it('один код НАЕ в двох рядках розкриваються незалежно', async () => {
    getAirLimits.mockResolvedValue(card({ sources: [card().sources[0]!, { ...card().sources[0]!, id: 77, region: 'MANGISTAU' }] }));
    render(mount(true));
    await screen.findAllByText('Source 1085');
    const toggles = screen.getAllByRole('button', { name: 'Substances of source 1085' });
    fireEvent.click(toggles[0]!);
    expect(toggles[0]!.getAttribute('aria-expanded')).toBe('true');
    expect(toggles[1]!.getAttribute('aria-expanded')).toBe('false');
  });

  it('фільтр і «нічого не знайдено»', async () => {
    render(mount(true));
    await screen.findByText('Source 1085');
    const filter = screen.getByRole('textbox', { name: text('permits.air.filter.label') });
    fireEvent.change(filter, { target: { value: 'wellheads' } });
    expect(screen.getAllByTestId('air-source-row')).toHaveLength(1);
    fireEvent.change(filter, { target: { value: 'zzz' } });
    expect(screen.getByText(text('permits.air.filter.none'))).toBeTruthy();
  });

  it('підсумки по речовинах', async () => {
    render(mount(true));
    await screen.findByText('Source 1085');
    fireEvent.click(screen.getByRole('radio', { name: text('permits.air.view.totals') }));
    const table = screen.getByRole('table');
    expect(within(table).getByText('0333')).toBeTruthy();
    expect(within(table).getByText('Hydrogen sulphide')).toBeTruthy();
  });

  it('кнопка імпорту веде на майстер; без права запису її немає', async () => {
    const { unmount } = render(mount(true));
    await screen.findByText('Source 1085');
    expect(screen.getByRole('link', { name: text('permits.air.import') }).getAttribute('href')).toBe('/admin/permits/5/import');
    unmount();
    render(mount(false));
    await screen.findByText('Source 1085');
    expect(screen.queryByRole('link', { name: text('permits.air.import') })).toBeNull();
  });

  it('порожня картка: пояснення з дією імпорту або лише читання', async () => {
    getAirLimits.mockResolvedValue(card({ sources: [], substanceRows: 0, totals: [] }));
    const { unmount } = render(mount(true));
    expect(await screen.findByText(text('permits.air.empty.title'))).toBeTruthy();
    expect(screen.getByText(text('permits.air.empty.text'))).toBeTruthy();
    unmount();
    render(mount(false));
    expect(await screen.findByText(text('permits.air.empty.readOnly'))).toBeTruthy();
  });

  it('помилка читання - повідомлення, а не порожня картка', async () => {
    getAirLimits.mockRejectedValue(new Error('boom'));
    render(mount(true));
    expect(await screen.findByRole('alert')).toBeTruthy();
    expect(screen.queryByTestId('air-counts')).toBeNull();
  });
});
