import { readFileSync } from 'node:fs';
import path from 'node:path';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { createMemoryRouter, RouterProvider } from 'react-router-dom';
import { theme } from '@/shared/theme/theme';
import { AppLayout } from '@/app/AppLayout';
import { NavbarDesktopBreakpoint, NavbarDesktopQuery } from '@/app/navbarBreakpoint';

/**
 * RC19: меню на ширині 768–899 px.
 *
 * ⛔ Дефект 1: межа «меню — колонка» була 48em (768 px) — колонка 260 px з'їдала третину ширини.
 * ⛔ Дефект 2 (аудит P1-1): після перенесення межі на 900 бургер лишався `hiddenFrom="sm"`, і на
 * 768–899 меню було згорнуте без кнопки відкриття. Тепер видимість бургера й кнопки згортання
 * рахується ТИМ САМИМ запитом, що й `AppShell.navbar.breakpoint`.
 */
afterEach(() => vi.unstubAllGlobals());

function stubViewport(width: number): string[] {
  const queries: string[] = [];

  vi.stubGlobal('matchMedia', (query: string) => {
    queries.push(query);
    const min = /min-width:\s*(\d+(?:\.\d+)?)px/.exec(query);

    return {
      matches: min === null ? false : width >= Number(min[1]),
      media: query,
      onchange: null,
      addEventListener: () => undefined,
      removeEventListener: () => undefined,
      addListener: () => undefined,
      removeListener: () => undefined,
      dispatchEvent: () => false,
    };
  });

  return queries;
}

function renderShell(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(() =>
      Promise.resolve(
        new Response(
          JSON.stringify({
            userId: 1,
            userName: 'tester',
            language: 'en',
            permissions: ['Registry.View'],
            isSimulation: false,
            mustChangePassword: false,
          }),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        ),
      ),
    ),
  );
  const router = createMemoryRouter(
    [{ path: '/', element: <AppLayout />, children: [{ index: true, element: <div>Page</div> }] }],
    { initialEntries: ['/'] },
  );

  render(
    <MantineProvider theme={theme}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <RouterProvider router={router} />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

describe('AppLayout: меню 768–899 px', () => {
  it('межа — 900 px, запит хука будується з тієї самої константи', () => {
    expect(NavbarDesktopBreakpoint).toBe(900);
    expect(NavbarDesktopQuery).toBe('(min-width: 900px)');
  });

  it('на 768 px є бургер і немає кнопки згортання колонки', async () => {
    const queries = stubViewport(768);
    renderShell();

    expect(await screen.findByRole('button', { name: /nav\.menu/ })).toBeTruthy();
    expect(screen.queryByRole('button', { name: /nav\.collapse/ })).toBeNull();
    expect(queries).toContain(NavbarDesktopQuery);
  });

  it('на 899 px бургер ще є', async () => {
    stubViewport(899);
    renderShell();

    expect(await screen.findByRole('button', { name: /nav\.menu/ })).toBeTruthy();
    expect(screen.queryByRole('button', { name: /nav\.collapse/ })).toBeNull();
  });

  it('на 900 px бургера немає, є кнопка згортання колонки', async () => {
    stubViewport(900);
    renderShell();

    expect(await screen.findByRole('button', { name: /nav\.collapse/ })).toBeTruthy();
    expect(screen.queryByRole('button', { name: /nav\.menu/ })).toBeNull();
  });

  it('у вихідному коді не лишилось hiddenFrom/visibleFrom="sm" для меню', () => {
    const source = readFileSync(path.resolve(__dirname, '../AppLayout.tsx'), 'utf8');

    expect(source).not.toContain('hiddenFrom="sm"');
    expect(source).not.toContain('visibleFrom="sm"');
    expect(source).toContain('breakpoint: NavbarDesktopBreakpoint,');
  });
});
