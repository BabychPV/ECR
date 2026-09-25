import { describe, it, expect, afterEach, vi } from 'vitest';
import { configure, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { QueryClientProvider, QueryClient } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { theme } from '@/shared/theme/theme';
import { SecurityPage } from '@/pages/admin/SecurityPage';
import { withTestDefaults } from '@/test/render';

configure({ asyncUtilTimeout: 20_000 });

/**
 * `X-07`: перелік користувачів мовчки обрізався на 200 (`?limit=200`).
 * `X-06`: рядок неактивного користувача мав `opacity: 0.5` разом із кнопками —
 * контраст тексту й дій падав нижче AA.
 */

const user = (id: number, isActive = true) => ({
  id,
  userName: `user.${String(id)}`,
  displayName: `User ${String(id)}`,
  provider: 'Local',
  email: null,
  isActive,
  isBootstrapAdmin: false,
  isLockedOut: false,
  mustChangePassword: false,
  receivesAlerts: false,
  lastSignInAt: null,
});

const requested: string[] = [];

function stubFetch(): void {
  requested.length = 0;

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      const json = (body: unknown): Response =>
        new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });

      if (url.includes('/api/v1/me')) {
        return json({
          userId: 99, userName: 'admin', language: 'en', permissions: ['Security.ManageUsers'],
          grants: {}, denies: [], isSimulation: false, simulatedForUserId: null, mustChangePassword: false,
        });
      }

      if (url.includes('/api/v1/users')) {
        requested.push(url);

        return url.includes('cursor=page2')
          ? json({ items: [user(3)], nextCursor: null, totalCount: 3 })
          : json({ items: [user(1), user(2, false)], nextCursor: 'page2', totalCount: 3 });
      }

      return json([]);
    }),
  );
}

function show(): void {
  render(
    <MantineProvider theme={withTestDefaults(theme)}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <MemoryRouter initialEntries={['/admin/security?tab=users']}>
          <SecurityPage />
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('SecurityPage: перелік користувачів', { timeout: 60_000 }, () => {
  it('X-07: є ще сторінка — сказано, скільки показано, і решту можна довантажити', async () => {
    stubFetch();
    show();

    expect(await screen.findByText('user.1')).toBeTruthy();

    // ⛔ Мутація «повернути одноразовий `?limit=200`» — ні підсумку, ні дії.
    expect(screen.getByText('⟦common.shownOf (shown=2, total=3)⟧')).toBeTruthy();
    expect(screen.queryByText('user.3')).toBeNull();

    await userEvent.click(screen.getByRole('button', { name: '⟦documents.more⟧' }));

    expect(await screen.findByText('user.3')).toBeTruthy();
    await waitFor(() => expect(requested.some((url) => url.includes('cursor=page2'))).toBe(true));

    // Усе показано — кнопки більше немає.
    await waitFor(() => expect(screen.queryByRole('button', { name: '⟦documents.more⟧' })).toBeNull());
  });

  it('X-06: неактивний рядок не прозорий — позначений бейджем', async () => {
    stubFetch();
    show();

    const login = await screen.findByText('user.2');
    const row = login.closest('tr') as HTMLElement;

    // ⛔ Мутація «повернути `opacity={isActive ? 1 : 0.5}`» дає тут `0.5`.
    expect(row.style.opacity).toBe('');
    expect(row.getAttribute('data-inactive')).toBe('');
    expect(row.textContent).toContain('⟦security.inactive⟧');
  });
});
