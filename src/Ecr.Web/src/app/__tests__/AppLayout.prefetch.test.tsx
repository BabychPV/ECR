import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { createMemoryRouter, RouterProvider } from 'react-router-dom';
import { theme } from '@/shared/theme/theme';
import { AppLayout } from '@/app/AppLayout';

/**
 * Прогрів за наміром у навбарі (`PR nav-arch #5`, директива C2) —
 * наскрізна перевірка через СПРАВЖНІЙ `AppLayout` (не ізольований
 * `useRoutePrefetch.test.tsx`): пункт навбару справді існує в DOM, наведення
 * на нього справді викликає мережевий запит на дані цільової сторінки.
 *
 * ⚠ `TemplatesPage` замокана легкою заглушкою (`routePrefetch.test.ts`
 * пояснює чому): цей файл не рендерить саму сторінку взагалі (лише навбар
 * `AppLayout`), тож справжній компонент тут і не потрібен — а прогрів
 * усе одно (fire-and-forget) реально викликає її код-чанк, чиє важке
 * піддерево залежностей під повним прогоном `npm test` стабільно
 * перевищувало тестовий таймаут 5000 мс.
 */
vi.mock('@/pages/admin/TemplatesPage', () => ({ TemplatesPage: () => null }));

const AdminMeResponse = {
  userId: 1,
  userName: 'tester',
  language: 'en',
  permissions: ['Template.Edit', 'Registry.View'],
  isSimulation: false,
  mustChangePassword: false,
};

const OperatorMeResponse = {
  ...AdminMeResponse,
  permissions: [],
};

function jsonResponse(body: unknown): Response {
  return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
}

function stubFetch(me: typeof AdminMeResponse, fetchMock: ReturnType<typeof vi.fn>): void {
  vi.stubGlobal(
    'fetch',
    fetchMock.mockImplementation(async (input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('/api/v1/me')) return jsonResponse(me);
      if (url.includes('/ui-strings/')) return jsonResponse({ languageCode: 'en', revision: 1, strings: {} });
      if (url.includes('/api/v1/templates')) {
        return jsonResponse({ items: [{ id: 1, code: 'TPL1' }], nextCursor: null, totalCount: 1 });
      }

      return jsonResponse(null);
    }),
  );
}

function renderAppLayout(): ReturnType<typeof render> {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const router = createMemoryRouter(
    [
      {
        path: '/',
        element: <AppLayout />,
        children: [{ index: true, element: <div data-testid="page-content">Main</div> }],
      },
    ],
    { initialEntries: ['/'] },
  );

  return render(
    <MantineProvider theme={theme}>
      <QueryClientProvider client={client}>
        <RouterProvider router={router} />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

describe('AppLayout — навбар прогріває маршрут за наміром (PR nav-arch #5)', () => {
  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });

  it('наведення (mouseEnter) на пункт "Templates" запускає запит /api/v1/templates ПІСЛЯ затримки наміру', async () => {
    const fetchMock = vi.fn();
    stubFetch(AdminMeResponse, fetchMock);

    renderAppLayout();

    // ⚠ Реальним часом дочекатись сесії/каталогу/навбару (той самий прийом,
    // що й `AppLayout.skiplink.test.tsx`) — і лише ПІСЛЯ цього перемкнутись
    // на фальшивий таймер заради самої затримки наміру: `findByRole`
    // усередині покладається на СПРАВЖНІ таймери для власного очікування,
    // і ввімкнути фальшиві раніше означало б, що воно ніколи не
    // спрацьовує.
    const templatesLink = await screen.findByRole('link', { name: /templates/i });

    vi.useFakeTimers();
    const callsBeforeHover = fetchMock.mock.calls.length;

    fireEvent.mouseEnter(templatesLink);

    // Одразу після наведення — ще нічого (затримка наміру не минула).
    expect(
      fetchMock.mock.calls.slice(callsBeforeHover).some((call) => String(call[0]).includes('/api/v1/templates')),
    ).toBe(false);

    await vi.advanceTimersByTimeAsync(150);

    expect(
      fetchMock.mock.calls.slice(callsBeforeHover).some((call) => String(call[0]).includes('/api/v1/templates')),
    ).toBe(true);

    // Той самий hover прогріває й код-чанк `TemplatesPage` (fire-and-forget)
    // — дочекатись його явно, щоб не лишити необроблений `import()` після тесту.
    await import('@/pages/admin/TemplatesPage');
  });

  it('користувач БЕЗ права Template.Edit не бачить пункт "Templates" узагалі — прогрівати нічого', async () => {
    const fetchMock = vi.fn();
    stubFetch(OperatorMeResponse, fetchMock);

    renderAppLayout();

    // Домашній пункт (без права) — орієнтир, що навбар уже домалювався.
    await screen.findByRole('link', { name: /documents/i });

    // ⚠ Немає елемента — немає й події наведення на нього: захист від
    // прогріву прихованих (за роллю) пунктів навбару — наслідок того самого
    // фільтра `navRoutes`, що й ховає їх від кліку (`Q-276`/`Q-277`), не
    // окремий механізм цієї картки.
    expect(screen.queryByRole('link', { name: /templates/i })).toBeNull();
  });
});
