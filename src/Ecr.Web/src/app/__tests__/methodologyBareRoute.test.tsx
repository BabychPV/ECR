import { describe, expect, it } from 'vitest';
import { render, screen } from '@testing-library/react';
import { createMemoryRouter, matchRoutes, RouterProvider, type RouteObject } from 'react-router-dom';
import { router } from '@/app/router';

/**
 * `/admin/methodologies/:id` (без `/versions`) не падає в «Page not found»:
 * знайдено на стенді тестувальником — список веде на `…/versions`, а голу
 * адресу набирають вручну.
 *
 * ⚠ Маршрут беремо з РЕАЛЬНОГО `router.routes` (як `functionalAreas.test.ts`),
 * а редирект проганяємо в мінімальному `createMemoryRouter` — `createBrowserRouter`
 * розрахований на `window.history`.
 */
describe('/admin/methodologies/:id → /versions', () => {
  it('голa адреса знаходить власний маршрут, а не пастку "*"', () => {
    const matches = matchRoutes(router.routes, '/admin/methodologies/7');
    const leaf = matches?.[matches.length - 1];

    expect(leaf?.route.path).toBe('methodologies/:id');
  });

  it('редирект замінює адресу на /admin/methodologies/:id/versions', async () => {
    const matches = matchRoutes(router.routes, '/admin/methodologies/7');
    const redirect = matches?.[matches.length - 1]?.route;

    expect(redirect?.element).toBeDefined();

    const routes: RouteObject[] = [
      { path: 'admin/methodologies/:id', element: redirect?.element },
      { path: 'admin/methodologies/:id/versions', element: <div data-testid="versions-screen">versions</div> },
    ];
    const memory = createMemoryRouter(routes, { initialEntries: ['/admin/methodologies/7'] });

    render(<RouterProvider router={memory} />);

    expect(await screen.findByTestId('versions-screen')).toBeTruthy();
    expect(memory.state.location.pathname).toBe('/admin/methodologies/7/versions');
    // replace: «назад» не повертає на адресу-редирект.
    expect(memory.state.historyAction).toBe('REPLACE');
  });
});
