import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import type { CurrentUserDto } from '@/api/types';
import { HomeRoute, startsOnReportsNotice } from '@/app/HomeRoute';
import { MeQueryKey } from '@/shared/session/useSession';
import { PageDescriptionContext } from '@/shared/ui/pageDescription';
import { theme } from '@/shared/theme/theme';

/**
 * ✎ P-1124-3 (D-NEW): роль без `Document.View`, але з `Report.ViewRegulatory` (вбудована `ReportViewer`) на головній
 * бачить пояснення «звіти — у SSRS», а не відмову переліку документів (зрізів у застосунку більше немає).
 *
 * ⛔ МУТАЦІЙНИЙ ДОКАЗ: у `HomeRoute` замінити умову на `false` — тест «роль переглядача звітів…» червоніє (на «/»
 * лишається перелік документів, тобто відмова).
 */

function meWith(permissions: string[]): CurrentUserDto {
  return {
    userId: 1,
    userName: 'tester',
    language: 'en',
    permissions,
    denies: [],
    grants: {},
    isSimulation: false,
    simulatedForUserId: null,
    mustChangePassword: false,
  };
}

function renderHome(me: CurrentUserDto): ReturnType<typeof render> {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  client.setQueryData(MeQueryKey, me);

  return render(
    <MantineProvider theme={theme}>
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={['/']}>
          <Routes>
            <Route
              path="/"
              element={
                // Як у роутері: маршрут / несе опис переліку документів (handle.descriptionKey).
                <PageDescriptionContext.Provider value="nav.documents.description">
                  <HomeRoute>
                    <div data-testid="documents">Documents list</div>
                  </HomeRoute>
                </PageDescriptionContext.Provider>
              }
            />
          </Routes>
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

describe('HomeRoute — стартова сторінка ролі без перегляду документів (P-1124-3)', () => {
  beforeEach(() => {
    // `staleTime: 0` у `useSession`: фоновий рефетч не має впасти на справжню мережу.
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => new Response(null, { status: 200, headers: { 'Content-Type': 'application/json' } })),
    );
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('роль переглядача звітів без Document.View — пояснення «звіти у SSRS», без посилань і без переліку', () => {
    renderHome(meWith(['Report.ViewRegulatory']));

    const notice = screen.getByTestId('reports-in-ssrs');
    expect(notice).toBeTruthy();
    expect(notice.textContent).toContain('home.reportsInSsrs.heading');
    // Мертвого посилання немає: адресу сервера звітів застосунок не знає.
    expect(notice.querySelector('a')).toBeNull();
    expect(screen.queryByTestId('documents')).toBeNull();
    // Опис маршруту / («відкрийте документ…») цій ролі не показується: документів у неї немає.
    expect(screen.queryByTestId('page-description')).toBeNull();
  });

  it('оператор із Document.View лишається на переліку документів (навіть із правом звітів)', () => {
    renderHome(meWith(['Document.View', 'Report.ViewRegulatory']));

    expect(screen.getByTestId('documents')).toBeTruthy();
    expect(screen.queryByTestId('reports-in-ssrs')).toBeNull();
  });

  it('без жодного з двох прав — головна без підміни', () => {
    renderHome(meWith(['Calculation.View']));

    expect(screen.getByTestId('documents')).toBeTruthy();
    expect(screen.queryByTestId('reports-in-ssrs')).toBeNull();
  });

  it('startsOnReportsNotice: профіль ще не завантажено — не підміняє', () => {
    expect(startsOnReportsNotice(undefined)).toBe(false);
  });
});
