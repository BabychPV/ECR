import { afterEach, describe, expect, it, vi } from 'vitest';
import { configure, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { testTheme } from '@/test/render';
import { RegistryImpactPage } from '../RegistryImpactPage';

/**
 * Вплив правки довідника (RT-25): стани «помилка», «немає права»,
 * «завантаження» переліку зачеплених (`ФВ-14.22`).
 *
 * ⚠ «Порожньо» вже покрито в `RegistryImpactPage.test.tsx` — тут не
 * дублюється. Тут додатково: при відмові чи в дорозі кнопка «перерахувати
 * все» неактивна — інакше людина ставила б перерахунок переліку, якого не
 * бачила.
 */
configure({ asyncUtilTimeout: 10_000 });

const Empty = '⟦registries.impact.empty⟧';

function serve(impact: () => Promise<Response>): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const path = String(input).split('?')[0] ?? '';

      if (path.endsWith('/impact')) return impact();

      return new Response(
        JSON.stringify({
          denies: [],
          grants: {},
          isSimulation: false,
          language: 'en',
          mustChangePassword: false,
          permissions: ['Registry.View', 'Calculation.Recalculate'],
          simulatedForUserId: null,
          userId: 9,
          userName: 'tester',
        }),
        { status: 200, headers: { 'Content-Type': 'application/json' } },
      );
    }),
  );
}

const problem = (status: number, errorCode: string): Promise<Response> =>
  Promise.resolve(
    new Response(JSON.stringify({ title: 'Error', status, errorCode, correlationId: 'c', detail: null }), {
      status,
      headers: { 'Content-Type': 'application/problem+json' },
    }),
  );

function show(): void {
  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <MemoryRouter initialEntries={['/admin/registries/COMPONENT/impact']}>
          <Routes>
            <Route path="/admin/registries/:code/impact" element={<RegistryImpactPage />} />
          </Routes>
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

const recalculateAll = async (): Promise<HTMLButtonElement> =>
  (await screen.findByRole('button', { name: /registries\.impact\.recalculateAll/ })) as HTMLButtonElement;

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('RegistryImpactPage — стани', () => {
  it('500: помилка з кодом, а не «зачеплених немає»; перерахунок неактивний', async () => {
    serve(() => problem(500, 'ECR-SYS-0500'));
    show();

    expect((await screen.findByRole('alert')).textContent).toContain('ECR-SYS-0500');
    expect(screen.queryByText(Empty)).toBeNull();
    expect(screen.queryByRole('table')).toBeNull();
    expect((await recalculateAll()).disabled).toBe(true);
  });

  it('403: «немає права» з кодом, а не «зачеплених немає»', async () => {
    serve(() => problem(403, 'ECR-AUTH-0403'));
    show();

    expect((await screen.findByRole('alert')).textContent).toContain('ECR-AUTH-0403');
    expect(screen.queryByText(Empty)).toBeNull();
  });

  it('у дорозі: «завантаження», а не «зачеплених немає»; перерахунок неактивний', async () => {
    serve(() => new Promise<Response>(() => undefined));
    show();

    expect((await screen.findByRole('status')).getAttribute('aria-busy')).toBe('true');
    expect(screen.queryByText(Empty)).toBeNull();
    expect(screen.queryByRole('table')).toBeNull();
    expect((await recalculateAll()).disabled).toBe(true);
  });
});
