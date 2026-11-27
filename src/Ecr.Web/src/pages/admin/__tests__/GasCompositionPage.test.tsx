import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, configure, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { loadCatalog } from '@/shared/i18n';
import { Catalog } from '@/test/__tests__/a11yFixtures';
import { testTheme } from '@/test/render';
import { GasCompositionPage } from '../GasCompositionPage';

/**
 * Сторінка «Склад газу» (D-NEW): перелік, пошук, смуга разового імпорту FLERT (лише поки потоків немає) і шторка потоку зі
 * шкалою версій.
 *
 * ⛔ Доказ: прибрати умову ``import.available`` у ``GasCompositionPage.tsx`` — «смуги імпорту немає, коли потоки є»
 * червоний; прибрати ``canImport`` з дій смуги — «без права імпорту кнопки немає» червоний.
 */
configure({ asyncUtilTimeout: 10_000 });

const json = (body: unknown, status = 200): Response =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });

const Stream = {
  id: 11,
  streamId: 317,
  number: '416',
  name: 'Units 331-332 flare',
  isOnshore: true,
  isSour: true,
  isHmb: true,
  hidden: false,
  caseCount: 2,
  versionCount: 2,
  latestFrom: '2022-01-01',
  deviatingVersions: 1,
  lowSumVersions: 0,
  noCompositionVersions: 0,
};

const Detail = {
  id: 11,
  streamId: 317,
  number: '416',
  name: 'Units 331-332 flare',
  isOnshore: true,
  isSour: true,
  isHmb: true,
  hmbDoc: null,
  group: 1,
  hidden: false,
  canEdit: true,
  cases: [
    {
      name: 'Units 331-332 - SOR Rev D04',
      versions: [
        {
          id: 101,
          validFrom: '2022-01-01',
          validTo: null,
          temperatureC: '45',
          densityOp: null,
          mwHysys: null,
          z: null,
          ruleParse: null,
          legacyTypeId: 1581,
          legacyCompositionId: 15810,
          flertAliases: 'Carbon_Monoxide=0.0169820000000000',
          sum: '99.9999999992626091',
          noComposition: false,
          components: [{ code: 'Methane', molPct: '99.9999999992626091' }],
          locks: [],
        },
      ],
    },
  ],
};

function serve(options: { streams?: unknown[]; available?: boolean; canImport?: boolean } = {}): ReturnType<typeof vi.fn> {
  const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input);
    if (url.includes('/ui-strings/')) return json({ languageCode: 'en', revision: 1, strings: Catalog });
    if (url.includes('/api/v1/me')) {
      return json({
        denies: [],
        grants: {},
        isSimulation: false,
        language: 'en',
        mustChangePassword: false,
        permissions: ['GasComposition.View', 'GasComposition.Edit'],
        simulatedForUserId: null,
        userId: 1,
        userName: 'tester',
      });
    }
    if (url.endsWith('/api/v1/gas-composition/import') && init?.method === 'POST') return json({ jobId: 'job-1' }, 202);
    if (url.endsWith('/api/v1/gas-composition/streams')) {
      return json({
        streams: options.streams ?? [Stream],
        canEdit: true,
        canImport: options.canImport ?? true,
        import: { available: options.available ?? false, lastRun: null },
      });
    }
    if (url.endsWith('/api/v1/gas-composition/streams/11')) return json(Detail);
    if (url.endsWith('/api/v1/gas-composition/components')) {
      return json([{ code: 'Methane', names: {}, formula: 'CH4', mw: null, group: 'hydrocarbon' }]);
    }
    return json(null);
  });
  vi.stubGlobal('fetch', fetchMock);
  return fetchMock;
}

function show(): void {
  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <MemoryRouter initialEntries={['/admin/gas-composition']}>
          <GasCompositionPage />
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('D-NEW: сторінка «Склад газу»', () => {
  it('перелік: Id, номер, назва і позначка Σ', async () => {
    serve();
    await loadCatalog('en', 'private');
    show();

    const name = await screen.findByRole('button', { name: Stream.name });
    const row = name.closest('tr')!;
    expect(within(row).getByText('317')).toBeTruthy();
    expect(within(row).getByText('416')).toBeTruthy();
    expect(within(row).getByText(Catalog['gasComposition.sumDeviating']!.replace('{count}', '1'))).toBeTruthy();
    expect(screen.queryByTestId('gas-import')).toBeNull();
  });

  it('смуга імпорту — лише поки потоків немає; кнопка ставить задачу', async () => {
    const fetchMock = serve({ streams: [], available: true });
    await loadCatalog('en', 'private');
    show();

    const banner = await screen.findByTestId('gas-import');
    fireEvent.click(within(banner).getByRole('button', { name: Catalog['gasComposition.import.button']! }));
    await waitFor(() =>
      expect(fetchMock.mock.calls.some(([url, init]) => String(url).endsWith('/gas-composition/import') && (init as RequestInit | undefined)?.method === 'POST')).toBe(true),
    );
  });

  it('без права імпорту кнопки немає, а пояснення є', async () => {
    serve({ streams: [], available: true, canImport: false });
    await loadCatalog('en', 'private');
    show();

    const banner = await screen.findByTestId('gas-import');
    expect(within(banner).queryByRole('button', { name: Catalog['gasComposition.import.button']! })).toBeNull();
    expect(within(banner).getByText(Catalog['gasComposition.import.noRight']!)).toBeTruthy();
  });

  it('шторка потоку: кейс, чинна версія, Σ і значення дублів FLERT у редакторі', async () => {
    serve();
    await loadCatalog('en', 'private');
    show();

    fireEvent.click(await screen.findByRole('button', { name: Stream.name }));
    const detail = await screen.findByTestId('gas-stream-detail');
    expect(within(detail).getByText('Units 331-332 - SOR Rev D04')).toBeTruthy();
    const row = within(detail).getByTestId('gas-version-row');
    // Місяць дії мовою інтерфейсу, а не ISO-дата.
    expect(within(row).getByText(/January 2022/)).toBeTruthy();
    expect(within(row).getByText(Catalog['gasComposition.current']!)).toBeTruthy();

    fireEvent.click(within(row).getByRole('button', { name: Catalog['gasComposition.edit']! }));
    expect(await screen.findByText(/Carbon_Monoxide=0\.0169820000000000/)).toBeTruthy();
  });
});
