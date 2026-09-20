import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { CreateDocumentModal } from '@/features/documents/CreateDocumentModal';
import { testTheme } from '@/test/render';

/**
 * Відмова верхніх переліків діалогу робила їх порожніми — і мовчала.
 *
 * ⛔ Проєкт і версія шаблону збиралися через `?? []`. Нижче в цьому ж файлі
 * уже стоїть `ErrorAlert` на `structure.error`, і коментар біля нього каже:
 * «`AsyncBoundary` тут не потрібна — „вантажиться“ вже видно по порожньому
 * переліку». Для АРКУШІВ це правда: їхню відмову показує той самий банер. Для
 * цих двох запитів — ні: порожній перелік однаково означав і «ще їде», і
 * «сервер відмовив», і другий випадок не показувало ніщо.
 *
 * ⚠ Наслідок той самий, що в `CreateProjectModal` (#449): людина читає
 * порожній перелік як «активних проєктів немає» або «опублікованих версій
 * немає» — і йде заводити ще один проєкт чи публікувати ще одну версію.
 */

const Refusal = {
  type: 'about:blank',
  title: 'Internal Server Error',
  status: 500,
  detail: 'перелік проєктів прочитати не вдалося',
  errorCode: 'ECR-SYS-0500',
  correlationId: 'cid-projects-1',
  // ⚠ Без ознаки подробиця до екрана не доходить (рішення людини про мову).
  messageKey: 'err.ECR-SYS-0500.unexpected',
};

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: {
      'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json',
    },
  });
}

function mockServer(projectsFail: boolean): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const path = String(input).split('?')[0] ?? '';

      // ⚠ `/api/v1/me` — ТОЧНО: `includes` збігся б і з `/api/v1/methodologies`.
      if (path.endsWith('/api/v1/me')) {
        return json({
          denies: [],
          grants: {},
          isSimulation: false,
          language: 'en',
          mustChangePassword: false,
          permissions: [],
          simulatedForUserId: null,
          userId: 1,
          userName: 'tester',
        });
      }

      if (path.endsWith('/api/v1/languages')) {
        return json([{ code: 'en', nameNative: 'English', isDefault: true }]);
      }

      if (path.endsWith('/api/v1/projects')) {
        return projectsFail
          ? json(Refusal, 500)
          : json({
              items: [{ id: 1, code: 'PRJ', status: 'Active' }],
              nextCursor: null,
              totalCount: 1,
            });
      }

      // ⚠ Вужчий маршрут — ПЕРЕД ширшим: `/templates/3/versions` містить
      // `/api/v1/templates` як префікс.
      if (/\/api\/v1\/templates\/\d+\/versions$/.test(path)) {
        return json({
          items: [{ id: 10, version: '1.0', status: 'Published', presentationRevision: 0 }],
          nextCursor: null,
          totalCount: 1,
        });
      }

      if (path.endsWith('/api/v1/templates')) {
        return json({
          items: [{ id: 3, code: 'AIR', nameL10n: { values: {} } }],
          nextCursor: null,
          totalCount: 1,
        });
      }

      return json(null);
    }),
  );
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter>
        <QueryClientProvider client={client}>
          <CreateDocumentModal opened onClose={() => {}} />
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('CreateDocumentModal: відмова джерел не виглядає як «нічого не заведено»', () => {
  it('проєкти не приїхали — на екрані причина з кодом, а не мовчазний порожній перелік', async () => {
    mockServer(true);
    show();

    const alert = await waitFor(() => screen.getByRole('alert'));

    expect(alert.textContent ?? '').toContain('перелік проєктів прочитати не вдалося');
    expect(alert.textContent ?? '').toContain('ECR-SYS-0500');
  });

  it('решта діалогу ЛИШАЄТЬСЯ робочою — відмова одного джерела не забирає форму', async () => {
    /*
     * ⚠ Спершу чекаємо на банер (момент, коли відмова вже в стані), і лише
     * ПІСЛЯ цього питаємо про поля. Інакше випадок лишався б зеленим і тоді,
     * коли код ховає весь діалог, — урок із #446.
     */
    mockServer(true);
    show();

    await waitFor(() => screen.getByRole('alert'));

    expect(screen.getByLabelText(/documents\.version/)).toBeDefined();
    expect(screen.getByRole('button', { name: /common\.save/ })).toBeDefined();
  });

  it('усе приїхало — банера немає', async () => {
    mockServer(false);
    show();

    // ⚠ Дочекатися саме наповненого переліку: запит у дорозі зробив би це
    // твердження зеленим на будь-якому коді.
    await waitFor(() => {
      expect(screen.getByLabelText(/documents\.project/)).toBeDefined();
    });

    expect(screen.queryByRole('alert')).toBeNull();
  });
});
