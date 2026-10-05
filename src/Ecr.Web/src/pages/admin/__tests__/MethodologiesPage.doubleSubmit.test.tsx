import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, fireEvent, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MethodologiesPage } from '@/pages/admin/MethodologiesPage';
import { testTheme } from '@/test/render';

/**
 * L9-37: «Run» у діалозі прогону без запису слав ДВА прогони на подвійний
 * клік чи повторний Enter. Спінер (`usePendingLoading`) вмикається лише через
 * 100 мс — до того кнопка активна, і другий клік ішов у `simulate.mutate`.
 */

const methodology = {
  id: 1,
  code: 'M1',
  nameL10n: { values: { en: 'Test Methodology' } },
  versions: [
    {
      id: 10,
      versionNumber: 1,
      level: 'Standard',
      status: 'Draft',
      effectiveFrom: null,
      numericMode: 'Actual',
      traceLevel: 'None',
    },
  ],
};

const json = (body: unknown): Response =>
  new Response(JSON.stringify(body), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  });

function mockFetch(): ReturnType<typeof vi.fn> {
  const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input);

    // ⚠ `endsWith`, НЕ `includes`: `/api/v1/methodologies` містить `/api/v1/me`.
    if (url.endsWith('/api/v1/me')) {
      return json({
        denies: [],
        grants: {},
        isSimulation: false,
        language: 'en',
        mustChangePassword: false,
        permissions: ['Calculation.View'],
        simulatedForUserId: null,
        userId: 1,
        userName: 'tester',
      });
    }

    // Прогін тримається «у польоті», доки тест не скінчиться.
    if (url.endsWith('/simulate') && init?.method === 'POST') return new Promise<Response>(() => {});

    if (url.includes('/api/v1/methodologies')) return json([methodology]);

    return json(null);
  });

  vi.stubGlobal('fetch', fetchMock);

  return fetchMock;
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter>
        <QueryClientProvider client={client}>
          <MethodologiesPage />
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('MethodologiesPage: подвійна відправка прогону без запису (L9-37)', () => {
  it('повторний клік «Run», поки прогін у польоті, другого запиту не шле', async () => {
    const fetchMock = mockFetch();
    show();

    fireEvent.click(await screen.findByRole('button', { name: '⟦methodologies.simulate⟧' }));
    const dialog = await screen.findByRole('dialog');
    const run = within(dialog).getByRole('button', { name: '⟦methodologies.run⟧' });

    fireEvent.click(run);

    // ⚠ Пауза менша за 100 мс `usePendingLoading`: спінера ще немає.
    await new Promise((resolve) => setTimeout(resolve, 10));
    fireEvent.click(run);
    await new Promise((resolve) => setTimeout(resolve, 10));

    // ⛔ Мутація «прибрати `if (simulate.isPending) return;`» — два POST, червоний.
    const simulateCalls = fetchMock.mock.calls.filter(([url]) => String(url).endsWith('/simulate'));
    expect(simulateCalls).toHaveLength(1);
  });
});
