import type { JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { createMemoryRouter, Outlet, RouterProvider } from 'react-router-dom';
import { Breadcrumbs, buildCrumbChain, type CrumbMatch } from '@/app/Breadcrumbs';
import { queryKeys } from '@/api/queryKeys';
import { routes } from '@/app/routes';

/**
 * Breadcrumbs (`PR nav-arch #3`).
 *
 * ⚠ `MantineProvider` обов'язковий (на відміну від `AdminLayout.test.tsx`,
 * `PR #2`): цей компонент рендерить справжні компоненти `@mantine/core`
 * (`Breadcrumbs`, `Skeleton`, `Anchor`), не голий `<Outlet/>`.
 *
 * Мутаційна перевірка (RED → GREEN, вручну, проведена перед комітом), для
 * ДВОХ окремих інвариантів:
 * 1. Поріг видимості. `chain.length < 2` тимчасово змінено на `< 1` —
 *    тест «маршрут з ОДНІЄЮ крихтою не показує breadcrumbs» упав (контейнер
 *    з'явився там, де мав лишитися відсутнім). Відновлено — GREEN.
 * 2. Поріг усічення. `TruncateFrom` тимчасово змінено з `4` на `5` — тест
 *    «ланцюжок з 4 рівнів усікає середину» впав (усі чотири крихти
 *    показались одразу, кнопки розкриття не було). Відновлено — GREEN.
 */
function client(): QueryClient {
  return new QueryClient({ defaultOptions: { queries: { retry: false } } });
}

function show(router: ReturnType<typeof createMemoryRouter>, queryClient: QueryClient): void {
  render(
    <MantineProvider>
      <QueryClientProvider client={queryClient}>
        <RouterProvider router={router} />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

function Layout(): JSX.Element {
  return <Outlet />;
}

/** Дерево `admin/templates/:id/versions/:versionId/relations` — 4 рівні (реєстр `routes.ts`). */
function relationsRouter(initialPath: string) {
  return createMemoryRouter(
    [
      {
        path: '/',
        element: <Layout />,
        children: [
          {
            path: 'admin',
            element: <Layout />,
            children: [
              {
                path: 'templates/:id',
                element: <Layout />,
                handle: routes.adminTemplateSection.handle,
                children: [
                  {
                    path: 'versions/:versionId/relations',
                    element: <Breadcrumbs />,
                    handle: routes.adminTemplateVersionRelations.handle,
                  },
                ],
              },
            ],
          },
        ],
      },
    ],
    { initialEntries: [initialPath] },
  );
}

/** Дерево `admin/registries/:code/definition` — 2 рівні (реєстр `routes.ts`). */
function registryRouter(initialPath: string) {
  return createMemoryRouter(
    [
      {
        path: '/',
        element: <Layout />,
        children: [
          {
            path: 'admin',
            element: <Layout />,
            children: [
              {
                path: 'registries/:code/definition',
                element: <Breadcrumbs />,
                handle: routes.adminRegistryDefinition.handle,
              },
            ],
          },
        ],
      },
    ],
    { initialEntries: [initialPath] },
  );
}

function seedTemplatesAndVersion(queryClient: QueryClient): void {
  queryClient.setQueryData(queryKeys.templates.list(), {
    items: [{ id: 1, code: 'TPL1', versionCount: 1 }],
    nextCursor: null,
    totalCount: 1,
  });
  queryClient.setQueryData(queryKeys.templates.versionsOf(1), {
    items: [
      {
        id: 7,
        version: '1.0',
        status: 'Draft',
        publishedAt: null,
        presentationRevision: 1,
        clonedFromVersionId: null,
      },
    ],
    nextCursor: null,
    totalCount: 1,
  });
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('buildCrumbChain — чиста функція (без рендера)', () => {
  it('резолвлює статичну й динамічну крихту, остання — current без href', () => {
    const qc = client();
    qc.setQueryData(queryKeys.registries.definition('EMISSIONS'), {
      code: 'EMISSIONS',
      id: 1,
      dataRevision: 1,
      definitionVersion: 1,
      fields: [],
      isTemporal: false,
      mappings: [],
      nameL10n: { values: { en: 'Emissions Registry' } },
      relations: [],
      rules: [],
      sourceKind: 'Manual',
    });

    const matches: CrumbMatch[] = [
      { pathname: '/admin/registries/EMISSIONS/definition', params: { code: 'EMISSIONS' }, handle: undefined },
      {
        pathname: '/admin/registries/EMISSIONS/definition',
        params: { code: 'EMISSIONS' },
        handle: routes.adminRegistryDefinition.handle,
      },
    ];

    const chain = buildCrumbChain(matches, qc);

    // ancestor 'admin-registries' (статична, з реєстру) + власна крихта (резолвлена).
    expect(chain).toHaveLength(2);
    expect(chain[1]!.text).toBe('Emissions Registry');
    expect(chain[1]!.current).toBe(true);
    expect(chain[1]!.href).toBeUndefined();
    expect(chain[0]!.current).toBe(false);
    expect(chain[0]!.href).toBe('/admin/registries');
  });

  it('матчі без валідного RouteHandle пропускаються (не ламають ланцюжок)', () => {
    const qc = client();
    const matches: CrumbMatch[] = [
      { pathname: '/', params: {}, handle: null },
      { pathname: '/admin', params: {}, handle: undefined },
    ];

    expect(buildCrumbChain(matches, qc)).toEqual([]);
  });
});

describe('Breadcrumbs — маршрут глибиною 2 (довідник)', () => {
  it('показує "Довідники" (статична, посилання на перелік) і резолвлену назву довідника (поточна, без посилання)', async () => {
    const queryClient = client();
    queryClient.setQueryData(queryKeys.registries.definition('EMISSIONS'), {
      code: 'EMISSIONS',
      id: 1,
      dataRevision: 1,
      definitionVersion: 1,
      fields: [],
      isTemporal: false,
      mappings: [],
      nameL10n: { values: { en: 'Emissions Registry' } },
      relations: [],
      rules: [],
      sourceKind: 'Manual',
    });

    show(registryRouter('/admin/registries/EMISSIONS/definition'), queryClient);

    const nav = await screen.findByTestId('breadcrumbs');
    const current = screen.getByText('Emissions Registry');
    expect(current.getAttribute('aria-current')).toBe('page');

    // Перший елемент — посилання на перелік довідників, не поточна крихта.
    const listLink = nav.querySelector('a');
    expect(listLink).not.toBeNull();
    expect(listLink!.getAttribute('href')).toBe('/admin/registries');

    // Немає кнопки розкриття — ланцюжок з 2 елементів коротший за поріг усічення.
    expect(screen.queryByRole('button', { name: 'Show all breadcrumbs' })).toBeNull();
  });

  it('запит сторінки ще виконується (реалістичне вікно до відповіді) — Skeleton, не порожнє місце і не сирий код', () => {
    const queryClient = client();
    // ⚠ Реалістичний сценарій: `RegistryConstructorPage` (сама сторінка,
    // не breadcrumbs) уже викликала свій `useQuery` для цього ж ключа й
    // чекає на відповідь — саме те «коротке вікно до відповіді сторінки»,
    // про яке каже акцептанс. Проміс навмисно не резолвиться в межах тесту:
    // важливий лише синхронний стан `fetchStatus: 'fetching'` одразу після
    // виклику.
    void queryClient.fetchQuery({
      queryKey: queryKeys.registries.definition('EMISSIONS'),
      queryFn: () => new Promise(() => {}),
    });

    show(registryRouter('/admin/registries/EMISSIONS/definition'), queryClient);

    expect(screen.getByTestId('crumb-skeleton')).toBeDefined();
    expect(screen.queryByText('EMISSIONS')).toBeNull();
  });

  it('холодний кеш БЕЗ активного запиту — статичний підпис, не вічний скелет і не сирий код', () => {
    show(registryRouter('/admin/registries/EMISSIONS/definition'), client());

    // ⚠ Різниця з тестом вище: тут жоден запит на цей ключ НЕ виконується
    // (наприклад, `RegistryConstructorPage` ще не встигла змонтуватися чи
    // взагалі не бере участі в цьому рендері). Вічний `Skeleton` тут був би
    // гіршим за статичний підпис — акцептанс забороняє порожнє місце й сирий
    // `:code`, але не забороняє статичний `labelKey` як БЕЗПЕЧНИЙ запасний
    // варіант, коли резолву справді нізвідки чекати.
    expect(screen.queryByTestId('crumb-skeleton')).toBeNull();
    expect(screen.getByText('⟦registries.constructor⟧')).toBeDefined();
  });
});

describe('Breadcrumbs — маршрут глибиною 4 (шаблон, версія, зв’язки), усічення середини', () => {
  it('за замовчуванням показує перший, …, передостанній і поточний — приховує середину', async () => {
    const queryClient = client();
    seedTemplatesAndVersion(queryClient);

    show(relationsRouter('/admin/templates/1/versions/7/relations'), queryClient);

    await screen.findByText('1.0');

    // Прихована крихта середини — резолвлена назва шаблону.
    expect(screen.queryByText('TPL1')).toBeNull();

    // Видно: перший (статичний "Templates"-ключ без каталогу — bracket-форма
    // `t()`), передостанній (версія, резолвлена) і поточний (Relations-ключ).
    expect(screen.getByRole('button', { name: 'Show all breadcrumbs' })).toBeDefined();
    expect(screen.getByText('1.0')).toBeDefined();

    const current = screen.getByText(/tables\.relationsTitle/);
    expect(current.getAttribute('aria-current')).toBe('page');
  });

  it('клік на кнопку розкриття показує повний ланцюжок, включно з прихованою назвою шаблону', async () => {
    const queryClient = client();
    seedTemplatesAndVersion(queryClient);
    const user = userEvent.setup();

    show(relationsRouter('/admin/templates/1/versions/7/relations'), queryClient);

    await screen.findByText('1.0');
    expect(screen.queryByText('TPL1')).toBeNull();

    await user.click(screen.getByRole('button', { name: 'Show all breadcrumbs' }));

    expect(screen.getByText('TPL1')).toBeDefined();
    expect(screen.queryByRole('button', { name: 'Show all breadcrumbs' })).toBeNull();
  });

  it('маршрут з ОДНІЄЮ крихтою (без предків) не показує breadcrumbs узагалі', () => {
    const queryClient = client();
    const router = createMemoryRouter(
      [
        {
          path: '/lone',
          element: <Breadcrumbs />,
          handle: { labelKey: 'x.y' },
        },
      ],
      { initialEntries: ['/lone'] },
    );

    show(router, queryClient);

    expect(screen.queryByTestId('breadcrumbs')).toBeNull();
  });
});

describe('Breadcrumbs — нуль нових HTTP-запитів (найважливіша вимога картки)', () => {
  it('не викликає fetch під час рендера й розкриття усіченого ланцюжка', async () => {
    const fetchSpy = vi.fn(() => {
      throw new Error('Breadcrumbs НЕ повинні викликати fetch — читання лише з кешу TanStack Query.');
    });
    vi.stubGlobal('fetch', fetchSpy);

    const queryClient = client();
    seedTemplatesAndVersion(queryClient);
    const user = userEvent.setup();

    show(relationsRouter('/admin/templates/1/versions/7/relations'), queryClient);

    await screen.findByText('1.0');
    await user.click(screen.getByRole('button', { name: 'Show all breadcrumbs' }));
    await screen.findByText('TPL1');

    expect(fetchSpy).not.toHaveBeenCalled();
  });
});
