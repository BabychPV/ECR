import { useEffect, useRef, type JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { createMemoryRouter, Link, RouterProvider } from 'react-router-dom';
import { theme } from '@/shared/theme/theme';
import { AppLayout } from '@/app/AppLayout';
import { formatDocumentTitle } from '@/app/useRouteTransitionFocus';
import { routeTransitionClassName, routeTransitionEnterClassName } from '@/app/motionTokens';

/**
 * `useRouteTransitionFocus` (`PR nav-arch #7`, директива B6/D):
 * `document.title` з ланцюжка breadcrumbs, резервний фокус на `<main>` (лише
 * коли сторінка не забрала фокус сама), CSS-перехід контейнера `<Outlet/>`
 * повністю вимкнений під `prefers-reduced-motion`.
 *
 * Мутаційна перевірка (RED → GREEN, вручну, перед комітом):
 * 1. Перевірку `prefersReducedMotion()` у `useRouteTransitionFocus.ts`
 *    тимчасово інвертовано (`if (!prefersReducedMotion()) return;`) — RED:
 *    «prefers-reduced-motion вимикає клас переходу ПОВНІСТЮ» впав (клас
 *    з'явився в дереві, коли рух мав бути вимкнений), а «...коли рух
 *    УВІМКНЕНО, клас з'являється» лишився зеленим (протилежний бік того
 *    самого твердження) — саме той парний доказ, що ловить і зняття
 *    перевірки, і її інверсію.
 * 2. Резервний фокус (`!main.contains(document.activeElement)`) тимчасово
 *    прибрано (`main.focus()` викликається БЕЗУМОВНО) — RED: «фокус
 *    сторінки з власним заголовком НЕ перебивається резервом» впав (фокус
 *    опинився на `<main>` замість заголовка сторінки).
 * Обидва рази відновлено оригінальний код — GREEN.
 */
const MeResponse = {
  userId: 1,
  userName: 'tester',
  language: 'en',
  permissions: [],
  isSimulation: false,
  mustChangePassword: false,
};

function jsonResponse(body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  });
}

function stubFetch(): void {
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
}

/** Домашня сторінка — простий вміст, без власного керування фокусом. */
function HomePage(): JSX.Element {
  return (
    <div data-testid="home-page">
      <Link to="/a">до А</Link>
    </div>
  );
}

/**
 * Сторінка з ВЛАСНИМ керуванням фокусом (той самий ідіом, що й
 * `PageHeader.tsx`) — саме те, що реальні 24 листові маршрути вже роблять:
 * резервний фокус нижче НЕ повинен перебивати це.
 */
function OwnHeadingPage(): JSX.Element {
  const heading = useRef<HTMLHeadingElement>(null);

  useEffect(() => {
    heading.current?.focus();
  }, []);

  return (
    <h2 ref={heading} tabIndex={-1} data-testid="own-heading">
      Сторінка А
    </h2>
  );
}

/** Сторінка БЕЗ власного керування фокусом — резервний фокус має спрацювати. */
function PlainPage(): JSX.Element {
  return <div data-testid="plain-page">Сторінка Б</div>;
}

function renderApp(): { router: ReturnType<typeof createMemoryRouter> } {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const router = createMemoryRouter(
    [
      {
        path: '/',
        element: <AppLayout />,
        children: [
          { index: true, element: <HomePage />, handle: { labelKey: 'home.label' } },
          { path: 'a', element: <OwnHeadingPage />, handle: { labelKey: 'a.label' } },
          { path: 'b', element: <PlainPage />, handle: { labelKey: 'b.label' } },
        ],
      },
    ],
    { initialEntries: ['/'] },
  );

  render(
    <MantineProvider theme={theme}>
      <QueryClientProvider client={client}>
        <RouterProvider router={router} />
      </QueryClientProvider>
    </MantineProvider>,
  );

  return { router };
}

/** Підміняє `matchMedia` для `(prefers-reduced-motion: reduce)`. */
function mockReducedMotion(reduced: boolean): void {
  vi.stubGlobal(
    'matchMedia',
    vi.fn((query: string) => ({
      matches: query.includes('prefers-reduced-motion') ? reduced : false,
      media: query,
      onchange: null,
      addEventListener: () => {},
      removeEventListener: () => {},
      addListener: () => {},
      removeListener: () => {},
      dispatchEvent: () => false,
    })),
  );
}

describe('formatDocumentTitle — та сама крихта, що й <Breadcrumbs/> (не друге джерело)', () => {
  it('порожній ланцюжок — лише назва застосунку', () => {
    expect(formatDocumentTitle([])).toBe('ECR');
  });

  it('крихти йдуть від НАЙГЛИБШОЇ до кореня, розділені " · ", із суфіксом ECR', () => {
    const chain = [{ text: 'Шаблони' }, { text: 'TPL1' }, { text: 'Версія 3.2' }];
    expect(formatDocumentTitle(chain)).toBe('Версія 3.2 · TPL1 · Шаблони · ECR');
  });

  it('крихти, що ще резолвяться (text: null), пропускаються, а не показуються як порожнеча', () => {
    const chain = [{ text: 'Шаблони' }, { text: null }];
    expect(formatDocumentTitle(chain)).toBe('Шаблони · ECR');
  });
});

describe('useRouteTransitionFocus: document.title (PR nav-arch #7)', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('оновлюється при вході на маршрут і знову — при переході на інший', async () => {
    stubFetch();
    const { router } = renderApp();

    await screen.findByTestId('home-page');
    expect(document.title).toBe('⟦home.label⟧ · ECR');

    await act(async () => {
      await router.navigate('/a');
    });

    expect(document.title).toBe('⟦a.label⟧ · ECR');
  });
});

describe('useRouteTransitionFocus: резервний фокус на <main> (PR nav-arch #7)', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('маршрут БЕЗ власного керування фокусом — фокус переходить на <main>', async () => {
    stubFetch();
    const { router } = renderApp();

    await screen.findByTestId('home-page');

    await act(async () => {
      await router.navigate('/b');
    });

    await screen.findByTestId('plain-page');
    expect(document.activeElement).toBe(document.getElementById('main-content'));
  });

  it('маршрут із ВЛАСНИМ керуванням фокусом (ідіом PageHeader) — резерв НЕ перебиває його', async () => {
    stubFetch();
    const { router } = renderApp();

    await screen.findByTestId('home-page');

    await act(async () => {
      await router.navigate('/a');
    });

    const heading = await screen.findByTestId('own-heading');
    expect(document.activeElement).toBe(heading);
    expect(document.activeElement).not.toBe(document.getElementById('main-content'));
  });

  it('зміна ЛИШЕ query-рядка (той самий pathname) не рухає фокус — не переривати дію користувача', async () => {
    stubFetch();
    const { router } = renderApp();

    await screen.findByTestId('home-page');

    await act(async () => {
      await router.navigate('/b');
    });
    await screen.findByTestId('plain-page');

    const main = document.getElementById('main-content');
    expect(document.activeElement).toBe(main);

    // Свідомо переносимо фокус ЗІ <main> — те, що фільтр таблиці (стан у
    // `searchParams`, директива B5) реально робить: користувач друкує в полі.
    const probe = document.createElement('input');
    document.body.appendChild(probe);
    probe.focus();
    expect(document.activeElement).toBe(probe);

    await act(async () => {
      await router.navigate('/b?x=1');
    });

    // ⛔ Резерв не мав спрацювати вдруге: pathname («/b») не змінився, лише
    // query-рядок — фокус лишається де був, а не стрибає назад на <main>.
    expect(document.activeElement).toBe(probe);

    probe.remove();
  });
});

describe('useRouteTransitionFocus: prefers-reduced-motion вимикає перехід ПОВНІСТЮ (PR nav-arch #7)', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('рух УВІМКНЕНО (matchMedia matches: false) — клас переходу з\'являється на навігації', async () => {
    mockReducedMotion(false);
    stubFetch();
    const { router } = renderApp();

    await screen.findByTestId('home-page');
    const container = document.querySelector(`.${routeTransitionClassName}`);
    expect(container).not.toBeNull();
    expect(container?.classList.contains(routeTransitionEnterClassName)).toBe(false);

    await act(async () => {
      await router.navigate('/a');
    });
    await screen.findByTestId('own-heading');

    expect(container?.classList.contains(routeTransitionEnterClassName)).toBe(true);
  });

  it('prefers-reduced-motion: reduce (matchMedia matches: true) — клас переходу НІКОЛИ не додається', async () => {
    mockReducedMotion(true);
    stubFetch();
    const { router } = renderApp();

    await screen.findByTestId('home-page');
    const container = document.querySelector(`.${routeTransitionClassName}`);
    expect(container).not.toBeNull();

    await act(async () => {
      await router.navigate('/a');
    });
    await screen.findByTestId('own-heading');

    // ⛔ Не «коротша» анімація — клас узагалі відсутній: рух вимкнено
    // ПОВНІСТЮ, як прямо вимагає директива, а не лише прискорено.
    expect(container?.classList.contains(routeTransitionEnterClassName)).toBe(false);

    await act(async () => {
      await router.navigate('/b');
    });
    await screen.findByTestId('plain-page');

    expect(container?.classList.contains(routeTransitionEnterClassName)).toBe(false);
  });
});
