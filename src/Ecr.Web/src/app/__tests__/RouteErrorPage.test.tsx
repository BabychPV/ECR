import type { JSX } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { createMemoryRouter, RouterProvider, type RouteObject } from 'react-router-dom';
import { AppLayout } from '@/app/AppLayout';
import { ErrorBoundary } from '@/app/ErrorBoundary';
import { RenderErrorCode } from '@/app/RenderErrorScreen';
import { router, withRenderErrorBoundary } from '@/app/router';
import { resetStaleVersion } from '@/app/staleVersion';
import { theme } from '@/shared/theme/theme';

/**
 * `D14-11`: помилка рендера не вбиває застосунок.
 *
 * ⛔ Дерево нижче збирається ТІЄЮ САМОЮ функцією, що й бойовий `router.tsx`
 * (`withRenderErrorBoundary`), а не власною копією її форми. Це навмисно:
 * копія лишалася б зеленою і тоді, коли межу з реального роутера прибрали б,
 * — тобто доводила б лише саму себе. Тому мутація «прибрати `errorElement`»
 * робиться в одному місці (`router.tsx`) і валить і поведінковий тест, і
 * структурний нижче.
 *
 * ⚠ `createMemoryRouter`, а не `router` напряму: бойовий `createBrowserRouter`
 * розрахований на справжню `window.history` — той самий аргумент, що вже
 * записаний у `routeGuards.router.test.tsx`.
 *
 * Мутаційна перевірка (виконана вручну, RED → GREEN): у
 * `withRenderErrorBoundary` прибрано `errorElement` — обидва тести цього
 * файлу падають (навбару в DOM немає: React Router підставляє власний
 * дефолтний екран замість УСЬОГО `AppLayout`), структурний падає на
 * `errorElement` === undefined. З межею — зелено.
 */

const MeResponse = {
  userId: 1,
  userName: 'tester',
  language: 'en',
  permissions: ['Template.Edit', 'Registry.View'],
  isSimulation: false,
  mustChangePassword: false,
};

function jsonResponse(body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  });
}

/** Сторінка, що падає при рендері — рівно той клас дефекту, який описує W-01. */
function Boom(): JSX.Element {
  throw new Error('падіння сторінки під час рендера');
}

function renderAppWithFailingPage(): ReturnType<typeof render> {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const memoryRouter = createMemoryRouter(
    [
      {
        path: '/',
        element: <AppLayout />,
        children: withRenderErrorBoundary([{ index: true, element: <Boom /> }]),
      },
    ],
    { initialEntries: ['/'] },
  );

  return render(
    <MantineProvider theme={theme}>
      <QueryClientProvider client={client}>
        <RouterProvider router={memoryRouter} />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

describe('D14-11 — помилка рендера сторінки', () => {
  beforeEach(() => {
    resetStaleVersion();

    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL) => {
        const url = String(input);

        if (url.includes('/api/v1/me')) return jsonResponse(MeResponse);
        if (url.includes('/ui-strings/')) {
          return jsonResponse({ languageCode: 'en', revision: 1, strings: {} });
        }

        return jsonResponse(null);
      }),
    );

    // React Router і React самі друкують перехоплену помилку — це очікувано
    // і не є частиною перевірки; глушимо, щоб stderr не читали як збій.
    vi.spyOn(console, 'error').mockImplementation(() => {});
    vi.spyOn(console, 'warn').mockImplementation(() => {});
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  it('каркас лишається живим: навігація в DOM, а замість сторінки — пояснення з кодом', async () => {
    renderAppWithFailingPage();

    // ⛔ Головне твердження: застосунок НЕ замінено цілком. Пункт навігації —
    // елемент, що живе в `AppLayout`, тобто ПОЗА межею помилки. Без
    // `errorElement` на дочірньому маршруті React Router підставляє свій
    // екран на місце кореня, і цього посилання в дереві немає взагалі.
    const nav = await screen.findByRole('navigation');
    expect(nav).toBeTruthy();
    expect(screen.getAllByRole('link').length).toBeGreaterThan(0);

    // ⚠ Саме `role="alert"`: помилку має ОГОЛОСИТИ читалка, а не лише
    // намалювати. Це та сама подача, що й скрізь (`shared/ui/ErrorAlert.tsx`).
    const alert = await screen.findByRole('alert');
    expect(alert.textContent).toContain(RenderErrorCode);

    // Технічний текст помилки доходить до екрана — «щось пішло не так»
    // заборонено (`07-checkpoints`, Етап 6).
    expect(alert.textContent).toContain('падіння сторінки під час рендера');

    // Ідентифікатор кореляції показується завжди — інакше скаргу користувача
    // нема з чим зіставити.
    expect(alert.querySelectorAll('code').length).toBeGreaterThanOrEqual(2);

    // Обидві дії, названі директивою, — на екрані.
    expect(screen.getByRole('button', { name: /reload page/i })).toBeTruthy();
    expect(screen.getByRole('button', { name: /document list/i })).toBeTruthy();
  });

  it('бойовий router.tsx справді несе цю межу над усіма дітьми AppLayout', () => {
    const root = router.routes.find((route) => route.path === '/');
    expect(root).toBeDefined();

    // ⚠ Рівно ОДИН безшляховий маршрут-обгортка — і всі маршрути застосунку
    // лежать під ним. Якби хтось додав маршрут повз обгортку (другим прямим
    // дитям кореня), цей маршрут лишився б без межі, а перевірка — зеленою,
    // тому перевіряється саме кількість прямих дітей.
    const children = root?.children ?? [];
    expect(children.length, 'усі маршрути мають лежати під однією межею помилки').toBe(1);

    const boundary = children[0] as RouteObject;
    expect(boundary.path, 'обгортка має бути безшляховою').toBeUndefined();
    expect(boundary.errorElement, 'межа помилки зникла з router.tsx').toBeDefined();

    // ⚠ Рахуються ВСІ нащадки, а не прямі діти: 17 адмінських сторінок лежать
    // під власним `AdminLayout`, тобто на рівень глибше. Число тут — нижня
    // межа «це справді все дерево застосунку», а не точний підрахунок:
    // прив'язка до точної кількості маршрутів валила б цей тест на кожному
    // новому екрані, нічого при цьому не доводячи.
    const descendants = (list: RouteObject[]): RouteObject[] =>
      list.flatMap((route) => [route, ...descendants(route.children ?? [])]);

    expect(descendants(boundary.children ?? []).length).toBeGreaterThan(20);
  });
});

/**
 * Останній рубіж (`main.tsx`): помилка ВИЩЕ за дерево маршрутів.
 *
 * Мутаційна перевірка (вручну, RED → GREEN): прибрано
 * `getDerivedStateFromError` з `ErrorBoundary` — тест падає (помилка виходить
 * назовні, `render` кидає). З ним — зелено.
 */
describe('D14-11 — класова межа в main.tsx', () => {
  beforeEach(() => {
    resetStaleVersion();
    vi.spyOn(console, 'error').mockImplementation(() => {});
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('падіння всього застосунку дає екран помилки, а не порожній корінь', () => {
    const { container } = render(
      <ErrorBoundary>
        <Boom />
      </ErrorBoundary>,
    );

    expect(container.innerHTML).not.toBe('');

    const alert = screen.getByRole('alert');
    expect(alert.textContent).toContain(RenderErrorCode);

    // ⛔ Екран малюється БЕЗ зовнішнього `MantineProvider` (тут його немає
    // навмисно — саме так виглядає падіння `App.tsx`): власний провайдер межі
    // і є тим, що не дає екрану помилки впасти самому.
    expect(screen.getByRole('button', { name: /reload page/i })).toBeTruthy();
  });
});
