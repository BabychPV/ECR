import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { testTheme } from '@/test/render';
import { PermitListsPage } from '@/pages/admin/PermitListsPage';
import { column, permitType, table } from '../../typeEditor/__tests__/fixtures';
import { makeType } from '../../types/__tests__/typesFixture';
import { usedCountText } from '../listsModel';
import { TypeFetchConcurrency } from '../useLists';

/**
 * Завантаження екрана «Lists» (PM-08d, доробка аудиту): визначення типів читаються з обмеженим паралелізмом, а
 * не всі разом (ліміт запитів 100/хв), і один недоступний тип не валить екран.
 *
 * Мутаційний доказ (вручну, відновлено): `inPool` із лімітом `items.length` (усі одразу) валить «не більш як
 * N одночасно»; прибраний `try/catch` у `queryFn` валить «404 на одному типі».
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

const json = (body: unknown, status = 200): Response =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });

const Count = 14;
const codes = Array.from({ length: Count }, (_, index) => `T${String(index + 1).padStart(2, '0')}`);

function definition(code: string) {
  return permitType({
    code,
    nameL10n: { en: `Type ${code}` },
    headerFields: [],
    tables: [table('Items', [column(`Method${code}`, { dataType: 'Lookup', listScope: 'TypeOnly', choices: ['A'] })])],
  });
}

interface Probe {
  inFlight: number;
  maxInFlight: number;
  detailCalls: number;
}

function stub(missing: string | null): Probe {
  const probe: Probe = { inFlight: 0, maxInFlight: 0, detailCalls: 0 };
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      const detail = /\/api\/v1\/permit-types\/([^/?]+)$/.exec(url);
      if (detail === null) {
        if (url.includes('/api/v1/registries')) return json([]);

        return json({ items: codes.map((code) => makeType(code)), nextCursor: null, totalCount: Count });
      }
      probe.detailCalls += 1;
      probe.inFlight += 1;
      probe.maxInFlight = Math.max(probe.maxInFlight, probe.inFlight);
      await new Promise((resolve) => setTimeout(resolve, 15));
      probe.inFlight -= 1;
      const code = detail[1] ?? '';

      return code === missing ? json({ title: 'not found' }, 404) : json(definition(code));
    }),
  );

  return probe;
}

function show(): void {
  render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter initialEntries={['/admin/permits/lists']}>
        <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
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
});

describe('Lists: завантаження визначень типів', () => {
  it(`${Count} типів: не більш як ${TypeFetchConcurrency} запити одночасно, кожен тип читається один раз`, async () => {
    const probe = stub(null);
    show();

    expect(await screen.findByText('MethodT14')).toBeTruthy();
    expect(probe.detailCalls).toBe(Count);
    expect(probe.maxInFlight).toBeLessThanOrEqual(TypeFetchConcurrency);
    expect(probe.maxInFlight).toBeGreaterThan(1);
  });

  it('404 на одному типі: екран живий, решту показано, про пропуск сказано', async () => {
    stub('T05');
    show();

    expect(await screen.findByText('MethodT14')).toBeTruthy();
    expect(screen.getByText('MethodT01')).toBeTruthy();
    expect(screen.queryByText('MethodT05')).toBeNull();
    await waitFor(() => expect(document.querySelector('[data-lists-types-failed]')).not.toBeNull());
    expect(document.querySelector('[data-lists-types-failed]')?.textContent).toContain('"count":1');
    expect(screen.queryByText('permits.lists.empty.title')).toBeNull();
  });
});

describe('usedCountText', () => {
  it('порожнє, одне й багато', () => {
    expect(usedCountText(0)).toBe('permits.lists.used.none');
    expect(usedCountText(1)).toBe('permits.lists.used.one');
    expect(usedCountText(3)).toBe('permits.lists.used.other {"count":3}');
  });
});