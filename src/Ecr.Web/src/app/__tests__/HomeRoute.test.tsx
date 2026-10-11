import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import type { CurrentUserDto } from '@/api/types';
import { HomeRoute, startsOnSnapshots } from '@/app/HomeRoute';
import { MeQueryKey } from '@/shared/session/useSession';
import { theme } from '@/shared/theme/theme';

/**
 * FP-1 (прохід «від першого обличчя» 2026-10-11, P1): роль без `Document.View`, але з
 * `Report.ViewRegulatory` (вбудована `ReportViewer`) стартує зі зрізів звітів, а не з
 * відмови на переліку документів.
 *
 * ⛔ МУТАЦІЙНИЙ ДОКАЗ: у `HomeRoute` замінити умову перенаправлення на `false` — тест
 * «роль зі зрізами…» червоніє (на «/» лишається перелік документів).
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
                <HomeRoute>
                  <div data-testid="documents">Documents list</div>
                </HomeRoute>
              }
            />
            <Route path="/admin/snapshots" element={<div data-testid="snapshots">Snapshots</div>} />
          </Routes>
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

describe('HomeRoute — стартова сторінка ролі без перегляду документів (FP-1)', () => {
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

  it('роль зі зрізами, без Document.View — перенаправлення на /admin/snapshots', () => {
    renderHome(meWith(['Report.ViewRegulatory', 'Report.ViewSnapshot', 'Report.Export']));

    expect(screen.getByTestId('snapshots')).toBeTruthy();
    expect(screen.queryByTestId('documents')).toBeNull();
  });

  it('оператор із Document.View лишається на переліку документів (навіть із правом зрізів)', () => {
    renderHome(meWith(['Document.View', 'Report.ViewRegulatory']));

    expect(screen.getByTestId('documents')).toBeTruthy();
    expect(screen.queryByTestId('snapshots')).toBeNull();
  });

  it('без жодного з двох прав — головна без мовчазного переходу', () => {
    renderHome(meWith(['Calculation.View']));

    expect(screen.getByTestId('documents')).toBeTruthy();
    expect(screen.queryByTestId('snapshots')).toBeNull();
  });

  it('startsOnSnapshots: профіль ще не завантажено — не перенаправляє', () => {
    expect(startsOnSnapshots(undefined)).toBe(false);
  });
});
