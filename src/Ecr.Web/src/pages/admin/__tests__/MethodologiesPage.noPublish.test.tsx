import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MethodologiesPage } from '@/pages/admin/MethodologiesPage';
import { testTheme } from '@/test/render';

/**
 * L9-43: публікація методології живе лише на екрані версій.
 *
 * ⛔ Перелік (`GET /api/v1/methodologies`) за побудовою віддає тільки
 * ОПУБЛІКОВАНІ версії, тож кнопка «Опублікувати» з умовою
 * `status !== 'Published'` тут не з'являлась ніколи — разом із діалогом
 * причини/дати, мутацією й діалогом diff це був мертвий код, що розходився з
 * живою копією в `MethodologyVersionsPage.tsx`.
 *
 * ⚠ Мок навмисно кладе в перелік ЧЕРНЕТКУ й дає право `Calculation.Publish` —
 * найсприятливіші для старої кнопки умови. Мутація «повернути блок кнопки
 * публікації» → кнопка з'являється, тест червоний.
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

function mockFetch(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      // ⚠ `endsWith`, НЕ `includes`: `/api/v1/methodologies` містить `/api/v1/me`.
      if (url.endsWith('/api/v1/me')) {
        return json({
          denies: [],
          grants: {},
          isSimulation: false,
          language: 'en',
          mustChangePassword: false,
          permissions: ['Calculation.View', 'Calculation.Publish'],
          simulatedForUserId: null,
          userId: 1,
          userName: 'tester',
        });
      }

      if (url.includes('/api/v1/methodologies')) return json([methodology]);

      return json(null);
    }),
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('MethodologiesPage: публікація — лише на екрані версій (L9-43)', () => {
  it('перелік не пропонує «Опублікувати», а веде до версій', async () => {
    mockFetch();

    render(
      <MantineProvider theme={testTheme}>
        <MemoryRouter>
          <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
            <MethodologiesPage />
          </QueryClientProvider>
        </MemoryRouter>
      </MantineProvider>,
    );

    const versions = await screen.findByRole('link', { name: '⟦methodologies.versionsTitle⟧' });
    expect(versions.getAttribute('href')).toBe('/admin/methodologies/1/versions');
    // Сусідня дія версії відмалювалась — тобто рядок версії на місці.
    expect(screen.getByRole('button', { name: '⟦methodologies.simulate⟧' })).toBeTruthy();

    expect(screen.queryByRole('button', { name: '⟦methodologies.publish⟧' })).toBeNull();
  });
});
