import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { createMemoryRouter, RouterProvider, type RouteObject } from 'react-router-dom';
import { loadCatalog } from '@/shared/i18n';
import { NotFoundPage } from '@/app/NotFoundPage';
import { router } from '@/app/router';
import { theme } from '@/shared/theme/theme';

/**
 * Невідома адреса під `/` показує сторінку застосунку, не дефолтний
 * розробницький екран React Router (`Q-302`).
 *
 * ⛔ Знайдено живим переходом на неіснуючу адресу під час UX-проходу, не
 * тестом: `router.tsx` не мав жодного `path: '*'`, тому React Router не
 * знаходив ЖОДНОГО збігу і показував власний, беззмістовний для реального
 * користувача екран («Unexpected Application Error! ... Hey developer 👋»).
 */
function stubCatalog(strings: Record<string, string>): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async () =>
      Promise.resolve(
        new Response(JSON.stringify({ languageCode: 'en', revision: 1, strings }), {
          status: 200,
          headers: { 'Content-Type': 'application/json', ETag: '"private-en-1"' },
        }),
      ),
    ),
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
  localStorage.clear();
});

describe('NotFoundPage', () => {
  async function renderResolved(): Promise<void> {
    stubCatalog({
      'nav.notFound.title': 'Page not found',
      'nav.notFound.hint': 'This address does not match any screen in this system.',
      'nav.documents': 'Documents',
    });

    await act(async () => {
      await loadCatalog('en', 'private');
    });

    render(
      <MantineProvider theme={theme}>
        <RouterProvider
          router={createMemoryRouter([{ path: '/', element: <NotFoundPage /> }], {
            initialEntries: ['/'],
          })}
        />
      </MantineProvider>,
    );
  }

  it('показує заголовок, підказку і посилання на головну', async () => {
    await renderResolved();

    const alert = screen.getByRole('alert');
    expect(alert.textContent).toContain('Page not found');
    expect(alert.textContent).toContain('This address does not match any screen in this system.');

    const link = screen.getByRole('link', { name: 'Documents' });
    expect(link.getAttribute('href')).toBe('/');
  });

  it('фокус переходить на заголовок відмови (клавіатурний прохід, ФВ-14.19)', async () => {
    await renderResolved();

    expect(document.activeElement?.textContent).toBe('Page not found');
  });
});

/**
 * ⚠ Реальне дерево `router.tsx` (не переспрощена копія): статична перевірка
 * форми `router.routes`, а не рендер — `createBrowserRouter` розрахований на
 * `window.history`, і саме тому `routeGuards.router.test.tsx` уже свідомо
 * НЕ рендерить його напряму (див. коментар там). Ця перевірка натомість
 * підтверджує, що каталог-конструктор дерева й СПРАВДІ несе `path: '*'`
 * серед дітей кореня — тобто що фікс живе в реальному router.tsx, а не лише
 * в ізольованому тестовому дереві вище.
 */
describe('router — каталог маршрутів застосунку', () => {
  it('корінь "/" несе дочірній маршрут-пастку "*" (Q-302)', () => {
    const root = router.routes.find((route) => route.path === '/');
    expect(root).toBeDefined();

    // ⚠ Пошук по ВСІХ нащадках, а не лише по прямих дітях: з `D14-11` між
    // `AppLayout` і його маршрутами стоїть безшляховий маршрут-обгортка з
    // `errorElement` (`withRenderErrorBoundary`, `router.tsx`). Перевірка й
    // далі про те саме — що пастка `*` живе в реальному дереві, — просто
    // більше не залежить від того, скільки рівнів угорі над нею.
    const descendants = (routes: RouteObject[]): RouteObject[] =>
      routes.flatMap((route) => [route, ...descendants(route.children ?? [])]);

    const catchAll = descendants(root?.children ?? []).find((route) => route.path === '*');
    expect(catchAll, 'router.tsx має втратити path: "*" серед дітей кореня "/"').toBeDefined();
  });
});
