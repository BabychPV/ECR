import { Suspense, type JSX } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { createMemoryRouter, Outlet, RouterProvider, type RouteObject } from 'react-router-dom';
import {
  ChunkReloadStorageKey,
  currentBuildId,
  isChunkLoadError,
  lazyWithReload,
  pageReload,
} from '@/app/chunkReload';
import { RenderErrorCode, StaleChunkErrorCode } from '@/app/RenderErrorScreen';
import { router, withRootErrorElement } from '@/app/router';
import { resetStaleVersion } from '@/app/staleVersion';
import { registerUnsavedSource } from '@/shared/ui/unsavedSources';
import { theme } from '@/shared/theme/theme';

/**
 * ✎ 25.11 (людина: «виправь це»): після оновлення на live відкрита вкладка показувала розробницький екран
 * React Router — «Unexpected Application Error! Failed to fetch dynamically imported module:
 * …/assets/IdleSignOut-….js … Hey developer 👋». Лінивий шматок КАРКАСА падав вище за межу сторінок, у корінь
 * `/`, де `errorElement` не було.
 *
 * ⛔ Дерево будується тією самою `withRootErrorElement`, що й бойовий `router.tsx`. Мутації (виконано вручну,
 * RED → GREEN): (1) `withRootErrorElement` повертає `routes` без змін — червоні тести «каркас падає…» (у DOM
 * «Unexpected Application Error!») і структурний; (2) у `RenderErrorScreen` прибрано виклик
 * `reloadOnceForStaleBuild` — червоний «відмова чанка → одне перезавантаження»; (3) у `reloadOnceForStaleBuild`
 * прибрано перевірку позначки збірки — червоний «вдруге не перезавантажує»; (4) прибрано перевірку
 * `hasUnsavedChanges` — червоний «незбережене не викидається».
 */

const ChunkMessage =
  'Failed to fetch dynamically imported module: http://localhost:6094/assets/IdleSignOut-BXE29SM.js';

/** Каркас, чий лінивий шматок не завантажився — рівно випадок live 25.11. */
function FailingShell({ error }: { error: Error }): JSX.Element {
  throw error;
}

function renderRoot(error: Error): ReturnType<typeof render> {
  const memoryRouter = createMemoryRouter(
    withRootErrorElement([
      {
        path: '/',
        element: <FailingShell error={error} />,
        children: [{ index: true, element: <p>page</p> }],
      },
    ]),
    { initialEntries: ['/'] },
  );

  return render(
    <MantineProvider theme={theme}>
      <RouterProvider router={memoryRouter} />
    </MantineProvider>,
  );
}

describe('✎ 25.11 — помилка каркаса: не розробницький екран React Router', () => {
  let reload: ReturnType<typeof vi.spyOn>;

  beforeEach(() => {
    resetStaleVersion();
    window.sessionStorage.clear();
    reload = vi.spyOn(pageReload, 'run').mockImplementation(() => {});
    vi.spyOn(console, 'error').mockImplementation(() => {});
    vi.spyOn(console, 'warn').mockImplementation(() => {});
  });

  afterEach(() => {
    window.sessionStorage.clear();
    resetStaleVersion();
    vi.restoreAllMocks();
  });

  it('каркас падає звичайною помилкою → дружній екран із кодом і згорнутими подробицями, без перезавантаження', async () => {
    const { container } = renderRoot(new Error('падіння каркаса'));

    expect(await screen.findByTestId('root-error-page')).toBeTruthy();
    expect(container.textContent).not.toContain('Unexpected Application Error');
    expect(container.textContent).not.toContain('Hey developer');

    const alert = screen.getByRole('alert');
    expect(alert.textContent).toContain(RenderErrorCode);
    expect(alert.textContent).not.toContain('падіння каркаса');

    const technical = screen.getByText('Error: падіння каркаса');
    expect(technical.closest('details')?.open).toBe(false);
    expect(screen.getByRole('button', { name: /reload page/i })).toBeTruthy();
    expect(screen.getByRole('main')).toBeTruthy();
    expect(reload).not.toHaveBeenCalled();
  });

  it('відмова чанка → екран «нова версія» і рівно одне автоматичне перезавантаження', async () => {
    const { container } = renderRoot(new TypeError(ChunkMessage));

    expect(await screen.findByTestId('root-error-page')).toBeTruthy();
    expect(container.textContent).not.toContain('Unexpected Application Error');
    expect(screen.getByRole('alert').textContent).toContain(StaleChunkErrorCode);

    await waitFor(() => expect(reload).toHaveBeenCalledTimes(1));
    expect(window.sessionStorage.getItem(ChunkReloadStorageKey)).toBe(currentBuildId());
    expect(await screen.findByTestId('render-error-reloading')).toBeTruthy();
  });

  it('вдруге в тій самій збірці не перезавантажує (без петлі) — лишає пояснення з кнопкою', async () => {
    window.sessionStorage.setItem(ChunkReloadStorageKey, currentBuildId());

    renderRoot(new TypeError(ChunkMessage));

    expect(await screen.findByTestId('root-error-page')).toBeTruthy();
    expect(screen.getByRole('alert').textContent).toContain(StaleChunkErrorCode);
    expect(screen.getByRole('button', { name: /reload page/i })).toBeTruthy();
    expect(screen.queryByTestId('render-error-reloading')).toBeNull();
    expect(reload).not.toHaveBeenCalled();
  });

  it('незбережене не викидається: з брудним джерелом автоматичного перезавантаження немає', async () => {
    const unregister = registerUnsavedSource('test-dirty', { hasUnsaved: () => true });

    try {
      renderRoot(new TypeError(ChunkMessage));

      expect(await screen.findByTestId('root-error-page')).toBeTruthy();
      expect(reload).not.toHaveBeenCalled();
      expect(window.sessionStorage.getItem(ChunkReloadStorageKey)).toBeNull();
    } finally {
      unregister();
    }
  });

  it('лінивий шматок каркаса (lazyWithReload) з відмовою чанка перезавантажує, а не падає в межу', async () => {
    const Piece = lazyWithReload<() => JSX.Element>(() => Promise.reject(new TypeError(ChunkMessage)));

    function Shell(): JSX.Element {
      return (
        <div>
          <Suspense fallback={<span>loading-piece</span>}>
            <Piece />
          </Suspense>
          <Outlet />
        </div>
      );
    }

    const memoryRouter = createMemoryRouter(withRootErrorElement([{ path: '/', element: <Shell /> }]), {
      initialEntries: ['/'],
    });
    render(
      <MantineProvider theme={theme}>
        <RouterProvider router={memoryRouter} />
      </MantineProvider>,
    );

    await waitFor(() => expect(reload).toHaveBeenCalledTimes(1));
    expect(screen.queryByTestId('root-error-page')).toBeNull();
    expect(screen.getByText('loading-piece')).toBeTruthy();
  });

  it('бойовий router.tsx: КОЖЕН маршрут верхнього рівня має errorElement', () => {
    expect(router.routes.length).toBeGreaterThanOrEqual(2);
    for (const route of router.routes as RouteObject[]) {
      expect(route.errorElement, `маршрут ${route.path ?? '(index)'} без межі помилки`).toBeDefined();
    }
  });
});

describe('isChunkLoadError — тексти рушіїв', () => {
  it.each([
    [new TypeError(ChunkMessage), true],
    [new TypeError('error loading dynamically imported module: /assets/x.js'), true],
    [new TypeError('Importing a module script failed.'), true],
    [new Error('Unable to preload CSS for /assets/x.css'), true],
    [Object.assign(new Error('Loading chunk 42 failed.'), { name: 'ChunkLoadError' }), true],
    [new Error("Cannot read properties of undefined (reading 'map')"), false],
    [null, false],
  ])('%s → %s', (error, expected) => {
    expect(isChunkLoadError(error)).toBe(expected);
  });
});
