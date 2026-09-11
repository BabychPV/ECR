import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { ApprovalRouteEditor } from '@/features/projects/ApprovalRouteEditor';

/**
 * Маршрут погодження проєкту (`Q-254`).
 *
 * ⛔ `route.error`/`roles.error` не рендерилися НІДЕ: невдалий запит і
 * дійсно порожній маршрут малювали той самий текст «кроків немає» — той
 * самий клас дефекту, що й `A7-04`.
 *
 * ⚠ `Select` («додати крок») підмінено легким заглушником: справжній
 * `@mantine/core` `Select`/`MultiSelect` під jsdom «зависає» (реальний,
 * відтворюваний факт — жоден наявний тест у репозиторії не рендерить ці два
 * компоненти напряму саме тому). Заглушник не бере участі в перевірці —
 * предмет цієї картки (`AsyncBoundary` навколо кроків) лежить цілком поза
 * ним.
 */
vi.mock('@mantine/core', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@mantine/core')>();

  return {
    ...actual,
    Select: () => null,
  };
});
/**
 * ⚠ `route` і `roles` — ДВА окремі запити, що йдуть майже одночасно: порядок
 * викликів `fetch` між ними не гарантований. Відповідь тому підбирається за
 * URL, а не за порядковим номером виклику — інакше тест міг би випадково
 * підсунути форму маршруту як відповідь на запит ролей (і навпаки) і впасти
 * не на своїй причині.
 */
function respondByUrl(byUrlSubstring: { match: string; body: unknown; status: number }[]): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: unknown) => {
      const url = typeof input === 'string' ? input : (input as Request).url;
      const found = byUrlSubstring.find((r) => url.includes(r.match));

      return Promise.resolve(
        new Response(JSON.stringify(found?.body), {
          status: found?.status ?? 200,
          headers: { 'Content-Type': 'application/problem+json' },
        }),
      );
    }),
  );
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider>
      <QueryClientProvider client={client}>
        <ApprovalRouteEditor projectId={1} />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('Маршрут погодження проєкту', () => {
  it('Q-254: невдалий запит маршруту показує помилку, а НЕ «кроків немає»', async () => {
    // Обидва запити (route + roles) мають впасти, щоб перевірити саме
    // об'єднану обробку помилки (`route.error ?? roles.error`).
    const refusal = {
      status: 503,
      body: {
        title: 'Недоступно',
        status: 503,
        errorCode: 'ECR-SYS-0503',
        correlationId: 'cid-route-1',
      },
    };
    respondByUrl([
      { match: 'approval-route', ...refusal },
      { match: '/roles', ...refusal },
    ]);
    show();

    screen.getByRole('button', { name: /workflow\.route/ }).click();

    // ⛔ Головне твердження: до виправлення тут був порожній `Stack` із
    // текстом «кроків немає» — той самий вигляд, що й у справді порожнього
    // маршруту, без жодного натяку на збій запиту.
    //
    // ⚠ Чекаємо САМЕ на текст коду помилки, а не на «якийсь алерт»: постійна
    // сіра підказка (`workflow.routeHint`) теж має `role="alert"` (так
    // влаштований `@mantine/core` Alert) і присутня ще ДО того, як запит
    // встигає впасти — `findAllByRole('alert')` побачив би її й завершився
    // зарано, не дочекавшись справжньої помилки.
    expect(await screen.findByText('ECR-SYS-0503')).toBeDefined();

    const alerts = screen.getAllByRole('alert');
    expect(alerts.map((a) => a.textContent).join(' ')).toContain('ECR-SYS-0503');
    expect(screen.queryByText(/workflow\.routeNone/)).toBeNull();
  });

  it('дійсно порожній маршрут пояснюється, а НЕ виглядає як помилка', async () => {
    respondByUrl([
      { match: 'approval-route', status: 200, body: { hasRoute: false, projectId: 1, steps: [] } },
      { match: '/roles', status: 200, body: [] },
    ]);
    show();

    screen.getByRole('button', { name: /workflow\.route/ }).click();

    expect(await screen.findByText(/workflow\.routeNone/)).toBeDefined();

    // ⛔ Лише постійна сіра підказка (`workflow.routeHint`) лишається
    // алертом — жодного КОДУ помилки серед них немає.
    const alerts = screen.getAllByRole('alert');
    expect(alerts.map((a) => a.textContent).join(' ')).not.toMatch(/ECR-|HTTP-\d/);
  });
});
