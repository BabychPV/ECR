import { afterEach, describe, expect, it, vi } from 'vitest';
import { configure, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { UiStringsPage } from '@/pages/admin/UiStringsPage';
import { testTheme } from '@/test/render';

/**
 * Редактор рядків інтерфейсу: стани переліку рядків і лічильників покриття
 * мов (`ФВ-14.22`).
 *
 * ⚠ Відмову переліку МОВ уже стереже `UiStringsPage.languagesError.test.tsx`.
 * Тут — перелік РЯДКІВ (каталог) і покриття (`BE-13`).
 *
 * ⛔ Дефект, який закрив останній випадок: відмова `GET /ui-strings/coverage`
 * не малювалася НІДЕ — лічильники просто зникали, тобто збій виглядав так
 * само, як сервер, що мов не назвав. На екрані, де перевіряють, скільки
 * перекладено, мовчазна відсутність лічильника читається як «рахувати нічого».
 */
configure({ asyncUtilTimeout: 10_000 });

const English = { 'a.key': 'Alpha', 'b.key': 'Beta' };

const ok = (body: unknown): Promise<Response> =>
  Promise.resolve(new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } }));

const problem = (status: number, errorCode: string): Promise<Response> =>
  Promise.resolve(
    new Response(
      JSON.stringify({
        title: status === 403 ? 'Forbidden' : 'Server error',
        status,
        errorCode,
        correlationId: 'corr-ui-strings',
        detail: null,
      }),
      { status, headers: { 'Content-Type': 'application/problem+json' } },
    ),
  );

const never = (): Promise<Response> => new Promise<Response>(() => undefined);

interface Plan {
  readonly catalog?: () => Promise<Response>;
  readonly coverage?: () => Promise<Response>;
}

function serve(plan: Plan): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      // ⚠ Порядок значущий: адреса покриття теж містить `/ui-strings/`.
      if (url.includes('/ui-strings/coverage')) {
        return (plan.coverage ?? (() => ok({ languages: [{ languageCode: 'en', total: 2, translated: 2, missing: 0 }] })))();
      }

      if (url.includes('/ui-strings/')) {
        return (plan.catalog ?? (() => ok({ languageCode: 'en', revision: 1, strings: English })))();
      }

      if (url.includes('/api/v1/languages')) return ok([{ code: 'en', nameNative: 'English' }]);

      throw new Error(`неочікуваний запит у тесті: ${url}`);
    }),
  );
}

function show(): void {
  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <MemoryRouter initialEntries={['/admin/ui-strings']}>
          <UiStringsPage />
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

/** Знайти банер відмови за кодом: `role="alert"` у дереві може бути не один. */
async function alertWithCode(code: string): Promise<HTMLElement> {
  return waitFor(() => {
    const found = screen.queryAllByRole('alert').find((node) => (node.textContent ?? '').includes(code));
    expect(found).toBeTruthy();
    return found as HTMLElement;
  });
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('UiStringsPage — стани переліку рядків', () => {
  it('500 каталогу: помилка з кодом, а не «рядків немає»', async () => {
    serve({ catalog: () => problem(500, 'ECR-SYS-0500') });
    show();

    await alertWithCode('ECR-SYS-0500');
    expect(screen.queryByText('⟦uiStrings.empty⟧')).toBeNull();
    expect(screen.queryByText('a.key')).toBeNull();
  });

  it('403 каталогу: «немає права» з кодом, а не «рядків немає»', async () => {
    serve({ catalog: () => problem(403, 'ECR-AUTH-0403') });
    show();

    await alertWithCode('ECR-AUTH-0403');
    expect(screen.queryByText('⟦uiStrings.empty⟧')).toBeNull();
  });

  it('порожній каталог: пояснення «рядків немає», без помилки й без рядків', async () => {
    serve({ catalog: () => ok({ languageCode: 'en', revision: 1, strings: {} }) });
    show();

    expect(await screen.findByText('⟦uiStrings.empty⟧')).toBeTruthy();
    expect(screen.queryByText('⟦uiStrings.emptyHint⟧')).toBeTruthy();
    expect(screen.queryByRole('alert')).toBeNull();
  });

  it('каталог у дорозі: «завантаження», а не «рядків немає»', async () => {
    serve({ catalog: never });
    show();

    expect((await screen.findByRole('status')).getAttribute('aria-busy')).toBe('true');
    expect(screen.queryByText('⟦uiStrings.empty⟧')).toBeNull();
  });
});

describe('UiStringsPage — стани покриття мов', () => {
  it('дзеркало: покриття приїхало — лічильники є, банера немає', async () => {
    serve({});
    show();

    expect((await screen.findByTestId('ui-strings-coverage')).textContent).toContain('language=en');
    await screen.findByText('a.key');
    expect(screen.queryByRole('alert')).toBeNull();
  });

  it('500 покриття: причина з кодом, а не мовчазна відсутність лічильників; редактор робочий', async () => {
    serve({ coverage: () => problem(500, 'ECR-SYS-0500') });
    show();

    // Редактор від покриття не залежить і лишається.
    expect(await screen.findByText('a.key')).toBeTruthy();

    await alertWithCode('ECR-SYS-0500');
    expect(screen.queryByTestId('ui-strings-coverage')).toBeNull();
  });
});
