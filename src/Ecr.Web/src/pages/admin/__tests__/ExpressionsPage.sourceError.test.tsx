import type { JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { ExpressionsPage } from '@/pages/admin/ExpressionsPage';
import { testTheme } from '@/test/render';

/**
 * Директива D15 §0, правило L10 — той самий клас, що #444/#446/#449/#451/#452,
 * #454 і #455.
 *
 * ⛔ Обидва переліки версій збиралися через `?? []`, тож відмова сервера
 * давала порожній `Select` — вигляд, невідрізненний від «версій немає». Ціна
 * не косметична: сторінка існує, щоб перевірити вираз ПРОТИ ОБРАНОЇ версії, і
 * сама попереджає (`Findings`), що без версії порожній перелік зауважень
 * означає лише «синтаксис цілий», а не «вираз правильний». Коли версія зникла
 * через відмову, людина отримує саме це слабке твердження — і читає як сильне.
 *
 * ⚠ Редактор виразів підмінено заглушкою НАВМИСНО: Monaco в jsdom коштує
 * хвилини (сусідній `expressions-page.render.test.tsx` тримає стелю 400 с), а
 * предмет цього файлу — які контроли сторінка малює, коли джерело відмовило.
 * Сам редактор перевіряється своїм набором (`features/expressions/__tests__`).
 */
vi.mock('@/features/expressions/ExpressionEditor', () => ({
  ExpressionEditor: (): JSX.Element => <div data-testid="expression-editor" />,
}));

/** ⚠ Без `messageKey` подробиця до екрана не доходить (рішення про мову). */
function refusal(detail: string, errorCode: string, correlationId: string): unknown {
  return {
    type: 'about:blank',
    title: 'Internal Server Error',
    status: 500,
    detail,
    errorCode,
    correlationId,
    messageKey: `err.${errorCode}.unexpected`,
  };
}

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: {
      'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json',
    },
  });
}

function mockServer(fail: { templates: boolean; methodologies: boolean }): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const path = String(input).split('?')[0] ?? '';

      if (path.includes('/ui-strings/')) {
        return json({ languageCode: 'en', revision: 1, strings: {} });
      }

      if (path.endsWith('/api/v1/methodologies')) {
        return fail.methodologies
          ? json(refusal('перелік методологій прочитати не вдалося', 'ECR-SYS-0503', 'cid-m-1'), 500)
          : json([
              {
                id: 3,
                code: 'AIR',
                nameL10n: { values: { en: 'Air' } },
                versions: [{ id: 30, versionNumber: '1.0', status: 'Published' }],
              },
            ]);
      }

      // ⚠ Вужчий маршрут — ПЕРЕД ширшим: `/templates/1/versions` містить
      // `/api/v1/templates` як префікс.
      if (/\/api\/v1\/templates\/\d+\/versions$/.test(path)) {
        return json({
          items: [
            {
              id: 10,
              version: '1.0.0.0',
              status: 'Published',
              presentationRevision: 0,
              clonedFromVersionId: null,
              publishedAt: '2026-09-12T00:00:00Z',
            },
          ],
          nextCursor: null,
          totalCount: 1,
        });
      }

      if (path.endsWith('/api/v1/templates')) {
        return fail.templates
          ? json(refusal('перелік шаблонів прочитати не вдалося', 'ECR-SYS-0500', 'cid-t-1'), 500)
          : json({
              items: [{ id: 1, code: 'GEN72571044', versionCount: 1 }],
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
      <QueryClientProvider client={client}>
        <ExpressionsPage />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

/** Перемикає діалект на «методологія» — там живе другий перелік. */
async function switchToMethodology(): Promise<void> {
  // ⚠ Каталог не завантажений, тож підпис приходить ключем у `⟦…⟧` — шукаємо
  // за ключем. Це ЄДИНИЙ `textbox` із таким підписом: підписи обох переліків
  // версій інші, а варіанти списку — не `textbox`.
  fireEvent.click(await screen.findByRole('textbox', { name: /expressions\.dialect/ }));
  fireEvent.click(await screen.findByRole('option', { name: /dialectMethodology/ }));
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('ExpressionsPage: відмова джерела версій ≠ «версій немає»', () => {
  it('шаблони не приїхали — причина з кодом, а порожнього переліку версій НЕМАЄ', async () => {
    mockServer({ templates: true, methodologies: false });
    show();

    const alert = await waitFor(() => screen.getByRole('alert'));

    expect(alert.textContent ?? '').toContain('перелік шаблонів прочитати не вдалося');
    expect(alert.textContent ?? '').toContain('ECR-SYS-0500');
    expect(screen.queryByRole('textbox', { name: /expressions\.templateVersion/ })).toBeNull();
  });

  it('шаблони приїхали — перелік версій на місці й доступний, банера немає', async () => {
    mockServer({ templates: false, methodologies: false });
    show();

    // ⚠ Спершу дочекатися ДОСТУПНОГО поля: доки версії в дорозі, воно
    // навмисно заблоковане, і перевірка «банера немає» на цьому кроці була б
    // зеленою на будь-якому коді.
    await waitFor(() => {
      const select = screen.getByRole('textbox', { name: /expressions\.templateVersion/ });
      expect((select as HTMLInputElement).disabled).toBe(false);
    });

    expect(screen.queryByRole('alert')).toBeNull();
  });

  it('методології не приїхали — причина з кодом, а порожнього переліку НЕМАЄ', async () => {
    mockServer({ templates: false, methodologies: true });
    show();
    await switchToMethodology();

    const alert = await waitFor(() => screen.getByRole('alert'));

    expect(alert.textContent ?? '').toContain('перелік методологій прочитати не вдалося');
    expect(screen.queryByRole('textbox', { name: /expressions\.methodologyVersion/ })).toBeNull();
  });
});
