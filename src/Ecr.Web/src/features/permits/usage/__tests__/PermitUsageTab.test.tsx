import type { JSX } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom';
import { apiFetch, EcrApiError } from '@/api/client';
import { PermitPage } from '@/pages/admin/PermitPage';
import { Catalog } from '@/test/__tests__/a11yFixtures';
import { TestProviders } from '@/test/render';
import * as permitsApi from '../../api/permitsApi';
import * as typesApi from '../../api/typesApi';
import * as usagesApi from '../../api/usagesApi';
import { permit, permitType } from '../../permit/__tests__/permitFixtures';
import { cellItem, headerItem, usage } from './usageFixtures';

/**
 * Вкладка «Used in» картки дозволу (PERMIT-USAGE): дані з `GET /permits/{id}/usages`, посилання на місце в документі,
 * смуга обрізаного переліку, порожньо / нічого за періодом / відмова з повтором, фільтр періоду в адресі (axe в обох
 * темах — `PermitUsageTab.a11y.test.tsx`). Тексти — справжні, із сіду.
 *
 * Мутаційні докази (виконано вручну, відновлено): у `PermitUsageTab` `periodKey === null ? {} : { periodKey }` → `{}`
 * валить «вибір періоду шле periodKey»; умова смуги `data?.truncated === true` → `false` валить «обрізаний перелік»;
 * `filtered={periodKey !== null}` → `false` валить «нічого за періодом».
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
vi.mock('@/features/permits/api/permitsApi', () => ({
  getPermit: vi.fn(),
  updatePermit: vi.fn(),
  activatePermit: vi.fn(),
  endPermit: vi.fn(),
  renewPermit: vi.fn(),
  deletePermit: vi.fn(),
}));
vi.mock('@/features/permits/api/typesApi', () => ({ getPermitType: vi.fn() }));
vi.mock('@/features/permits/api/usagesApi', () => ({ getPermitUsages: vi.fn() }));
vi.mock('@/shared/session/useSession', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/session/useSession')>()),
  useSession: () => ({ data: { permissions: ['Registry.View'], isSimulation: false } }),
}));

const getUsages = vi.mocked(usagesApi.getPermitUsages);

beforeEach(() => {
  vi.mocked(apiFetch).mockImplementation(() => Promise.resolve([]) as never);
  vi.mocked(typesApi.getPermitType).mockResolvedValue(permitType());
  vi.mocked(permitsApi.getPermit).mockResolvedValue(permit());
});
afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

function Probe(): JSX.Element {
  const location = useLocation();
  return <output data-testid="url">{location.pathname + location.search}</output>;
}

const url = (): string => screen.getByTestId('url').textContent ?? '';

function open(entry = '/admin/permits/5?tab=usage'): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <TestProviders>
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={[entry]}>
          <Routes>
            <Route
              path="/admin/permits/:id"
              element={
                <>
                  <PermitPage />
                  <Probe />
                </>
              }
            />
          </Routes>
        </MemoryRouter>
      </QueryClientProvider>
    </TestProviders>,
  );
}

describe('вкладка «Used in»', () => {
  it('вкладка є в смузі; перемикання ставить ?tab=usage і читає місця дозволу', async () => {
    getUsages.mockResolvedValue(usage([headerItem()]));
    open('/admin/permits/5');

    fireEvent.click(await screen.findByRole('tab', { name: 'Used in' }));
    expect(await screen.findByRole('heading', { name: 'Where this permit is used' })).toBeTruthy();
    expect(url()).toBe('/admin/permits/5?tab=usage');
    expect(getUsages).toHaveBeenCalledWith(5);
  });

  it('шапка і комірка: посилання на документ, «де», поле; лічильники зверху', async () => {
    getUsages.mockResolvedValue(usage([headerItem(), cellItem(), cellItem({ documentId: 7, documentKey: 'LAND-2026-07', periodKey: 202607 })]));
    open();

    const table = await screen.findByRole('table', { name: 'Places where the permit is used' });
    expect(screen.getByText('Documents: 2 · places: 3')).toBeTruthy();

    const rows = within(table).getAllByRole('row').slice(1);
    expect(rows).toHaveLength(3);
    const header = rows.find((row) => within(row).queryByText('Document header') !== null) as HTMLElement;
    expect(within(header).getByRole('link', { name: 'LAND-2026-06' }).getAttribute('href')).toBe('/documents/2');
    expect(within(header).getByText('Permit number')).toBeTruthy();

    const cell = rows.find((row) => within(row).queryByText('Row 3') !== null && within(row).queryByText('LAND-2026-06') !== null) as HTMLElement;
    expect(within(cell).getByText('Water · Water use')).toBeTruthy();
    expect(within(cell).getByRole('link', { name: 'LAND-2026-06' }).getAttribute('href')).toBe(
      '/documents/2?periodKey=202606&sheet=WATER&table=USE',
    );
  });

  it('обрізаний перелік: смуга каже, скільки показано з усіх місць', async () => {
    getUsages.mockResolvedValue(usage([cellItem()], { truncated: true, linkCount: 640, documentCount: 12 }));
    open();

    expect(await screen.findByText('Not every place is listed')).toBeTruthy();
    expect(screen.getByText('The list shows the first 1 of 640 places. Choose a period to narrow it.')).toBeTruthy();
  });

  it('повний перелік — смуги немає', async () => {
    getUsages.mockResolvedValue(usage([cellItem()]));
    open();

    await screen.findByRole('table', { name: 'Places where the permit is used' });
    expect(screen.queryByText('Not every place is listed')).toBeNull();
  });

  it('порожньо: пояснення, а не порожня таблиця', async () => {
    getUsages.mockResolvedValue(usage([]));
    open();

    expect(await screen.findByText('Not used in any document yet')).toBeTruthy();
    expect(screen.queryByRole('table', { name: 'Places where the permit is used' })).toBeNull();
    expect(screen.getByText('Documents: 0 · places: 0')).toBeTruthy();
  });

  it('відмова читання — помилка з повтором; повтор читає знову', async () => {
    getUsages.mockRejectedValueOnce(new EcrApiError({ title: 'Not found', status: 404, errorCode: 'ECR-PRM-0404', correlationId: 'c1' }));
    getUsages.mockResolvedValue(usage([headerItem()]));
    open();

    const retry = await screen.findByRole('button', { name: /Retry|Повторити/ });
    fireEvent.click(retry);
    expect(await screen.findByRole('table', { name: 'Places where the permit is used' })).toBeTruthy();
    expect(getUsages).toHaveBeenCalledTimes(2);
  });

  it('вибір періоду шле periodKey і ставить ?period=; «Show all periods» знімає фільтр', async () => {
    getUsages.mockImplementation((_id, query) =>
      Promise.resolve(query?.periodKey === 202605 ? usage([]) : usage([cellItem(), cellItem({ periodKey: 202605, rowKey: 'R9' })])),
    );
    open();
    await screen.findByRole('table', { name: 'Places where the permit is used' });

    fireEvent.click(screen.getByRole('textbox', { name: 'Period' }));
    const options = await screen.findAllByRole('option');
    expect(options.map((option) => option.textContent)).toEqual(['All periods', expect.stringMatching(/2026/), expect.stringMatching(/2026/)]);
    fireEvent.click(options[2] as HTMLElement);

    await waitFor(() => expect(getUsages).toHaveBeenCalledWith(5, { periodKey: 202605 }));
    expect(url()).toBe('/admin/permits/5?tab=usage&period=202605');
    expect(await screen.findByText('Not used in this period')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Show all periods' }));
    await waitFor(() => expect(url()).toBe('/admin/permits/5?tab=usage'));
    expect(await screen.findByRole('table', { name: 'Places where the permit is used' })).toBeTruthy();
  });

  it('перехід на іншу вкладку прибирає фільтр періоду з адреси', async () => {
    getUsages.mockResolvedValue(usage([]));
    open('/admin/permits/5?tab=usage&period=202606');
    await screen.findByText('Not used in this period');

    fireEvent.click(screen.getByRole('tab', { name: 'Overview' }));
    await waitFor(() => expect(url()).toBe('/admin/permits/5'));
  });
});
