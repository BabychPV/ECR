import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { theme } from '@/shared/theme/theme';
import { loadCatalog } from '@/shared/i18n';
import { TemplateVersionPage } from '@/pages/admin/TemplateVersionPage';

/**
 * Директива D15 §0, правило L10 — той самий клас, що #444/#446/#449/#451/#452,
 * #454/#455/#456/#457.
 *
 * ⛔ Стан версії рахується як `versionsList.data?.items.find(…)?.status`, тож
 * при відмові `GET /templates/{id}/versions` він `undefined` — рівно те саме
 * значення, що й «версії немає в переліку». Обидві кнопки
 * (`canPublish`/`canWithdraw`) через це ЗНИКАЛИ без жодного слова, і
 * адміністратор із правом `Template.Publish` читав екран як «версію вже
 * опубліковано» або «права немає».
 *
 * ⚠ Кнопки й далі сховані — коли стан невідомий, дія має деградувати в бік
 * ЗАБОРОНИ (той самий висновок, що в `PeriodsPage`, #452). Предмет цієї
 * перевірки — що замість мовчання тепер видно ПРИЧИНУ, і що банер бачить лише
 * той, хто взагалі може публікувати.
 */

const SeededStrings: Record<string, string> = {
  'version.title': 'Template version',
  'version.publish': 'Publish',
  'version.deprecate': 'Withdraw from use',
  'version.clone': 'Clone version',
  'version.relations': 'Table relations',
  'periodRules.title': 'Period access rules',
  'common.retry': 'Retry',
};

/** ⚠ Без `messageKey` подробиця до екрана не доходить (рішення про мову). */
const Refusal = {
  type: 'about:blank',
  title: 'Internal Server Error',
  status: 500,
  detail: 'перелік версій шаблону прочитати не вдалося',
  errorCode: 'ECR-SYS-0500',
  correlationId: 'cid-versions-1',
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

function stubFetch(options: { versionsFail: boolean; mayPublish: boolean }): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('/ui-strings/')) {
        return json({ languageCode: 'en', revision: 1, strings: SeededStrings });
      }

      if (url.includes('/me')) {
        return json({
          userId: 0,
          userName: 'test',
          language: 'en',
          permissions: options.mayPublish
            ? ['Template.Publish', 'Template.Edit', 'Template.View']
            : ['Template.Edit', 'Template.View'],
          isSimulation: false,
        });
      }

      if (url.includes('/structure')) {
        return json({
          isEditable: true,
          presentationRevision: 0,
          sheets: [],
          templateVersionId: 1,
        });
      }

      if (url.includes('/versions?limit=')) {
        return options.versionsFail
          ? json(Refusal, 500)
          : json({
              items: [
                {
                  id: 1,
                  version: '1.0.0.0',
                  status: 'Draft',
                  presentationRevision: 0,
                  clonedFromVersionId: null,
                  publishedAt: null,
                },
              ],
              nextCursor: null,
              totalCount: null,
            });
      }

      return json([]);
    }),
  );
}

beforeEach(() => {
  vi.stubGlobal(
    'ResizeObserver',
    class {
      observe(): void {}
      unobserve(): void {}
      disconnect(): void {}
    },
  );
});

afterEach(() => {
  vi.unstubAllGlobals();
});

async function renderPage(options: { versionsFail: boolean; mayPublish: boolean }): Promise<void> {
  stubFetch(options);
  await loadCatalog('en', 'public');
  await loadCatalog('en', 'private');

  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={theme}>
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={['/admin/templates/1/versions/1']}>
          <Routes>
            <Route
              path="/admin/templates/:id/versions/:versionId"
              element={<TemplateVersionPage />}
            />
          </Routes>
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

describe('TemplateVersionPage: відмова переліку версій ≠ «публікувати не можна»', () => {
  it('перелік версій не приїхав — причина з кодом на екрані, а не зниклі кнопки без слів', async () => {
    await renderPage({ versionsFail: true, mayPublish: true });

    // ⚠ Стеля 30 с, а не 400: банер або є одразу після відмови, або його немає
    // зовсім, і мутація має червоніти швидко.
    const alert = await waitFor(() => screen.getByRole('alert'), { timeout: 30_000 });

    expect(alert.textContent ?? '').toContain('перелік версій шаблону прочитати не вдалося');
    expect(alert.textContent ?? '').toContain('ECR-SYS-0500');

    /*
     * ⚠ Після приходу відмови — і лише після неї — стверджуємо про кнопки:
     * невідомий стан і далі закриває дію (деградація в бік заборони), просто
     * тепер про це сказано вголос.
     */
    expect(screen.queryByText('Publish')).toBeNull();
    expect(screen.queryByText('Withdraw from use')).toBeNull();
  });

  it('перелік приїхав — кнопка публікації на місці, банера немає', async () => {
    await renderPage({ versionsFail: false, mayPublish: true });

    // ⚠ Дзеркало: без нього «полагодити» можна було б банером, показаним завжди.
    expect(await screen.findByText('Publish')).not.toBeNull();
    expect(screen.queryByRole('alert')).toBeNull();
  });

  it('без права публікувати відмова цього переліку банера НЕ дає', async () => {
    await renderPage({ versionsFail: true, mayPublish: false });

    // ⚠ Ознака, що сесія й структура вже приїхали: «Clone version» від статусу
    // версії не залежить, зате залежить від права `Template.Edit`.
    await screen.findByText('Clone version');

    expect(screen.queryByRole('alert')).toBeNull();
  });
});
