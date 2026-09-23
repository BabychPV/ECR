import type { JSX } from 'react';
import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { DocumentPage } from '@/pages/DocumentPage';

/**
 * `BE-10`: панель заповненості СТОЇТЬ НА СТОРІНЦІ, а не просто існує у файлі.
 *
 * ⛔ Навіщо окремий набір поруч із `features/documents/__tests__/
 * sheet-fill-summary.test.tsx`. Той доводить поведінку САМОГО компонента —
 * дріб, крапка помилки, мовчання до відповіді — і лишиться зеленим, якщо
 * `<SheetFillSummary/>` прибрати з `DocumentPage` цілком. Рівно цей стан і
 * був у гілці до цього коміту: компонент, його тест і серверна дія були
 * написані й зелені, а користувач не бачив нічого. Тому тут перевіряється
 * інше твердження: сторінка документа показує число.
 *
 * ⚠ Сітка підмінена (`D1-12`, як у сусідніх наборах сторінки): RevoGrid тягне
 * власні запити й до заповненості стосунку не має.
 */
vi.mock('@/features/grid/DocumentGrid', () => ({
  DocumentGrid: (): JSX.Element => <div data-testid="grid-stub" />,
}));

vi.mock('@/features/methodologies/CalculationResultsPanel', () => ({
  CalculationResultsPanel: (): JSX.Element => <div data-testid="calc-stub" />,
}));

/**
 * Заповненість: одна таблиця з двох готова, і в другій є помилки.
 *
 * ⚠ Числа навмисно НЕ однакові й НЕ круглі: «1 / 2» неможливо отримати
 * випадково з довжини масиву, а `errorCount: 2` відрізняє «перевіряли, є
 * помилки» від `null` («не перевіряли»), за якого крапки не має бути зовсім.
 */
const Status = [
  { errorCount: 0, filledCells: 4, inputCells: 4, sheetCode: 'GEN', tableDefId: 1, warningCount: 0 },
  { errorCount: 2, filledCells: 1, inputCells: 6, sheetCode: 'GEN', tableDefId: 2, warningCount: 1 },
];

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

/**
 * @param statusResponse відповідь сервера саме на `/tables/status`.
 */
function mockFetch(statusResponse: () => Response): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('/api/v1/me')) {
        return jsonResponse({
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

      // ⛔ ПЕРЕД `/tables`: адреса заповненості підпадає і під нього теж, і
      // саме на цьому порядку вже спіткнулася заглушка `a11yFixtures` — там
      // панель отримала об'єкт зрізу замість масиву й повалила весь маршрут.
      if (url.includes('/tables/status')) return statusResponse();

      if (url.includes('/validation')) {
        return jsonResponse(
          { title: 'Not found', status: 404, detail: 'not validated', errorCode: 'ECR-DOC-0404' },
          404,
        );
      }

      if (url.includes('/tables')) {
        return jsonResponse([
          {
            allowsDynamicRows: false,
            maxDynamicRows: null,
            sheetCode: 'GEN',
            sheetDefId: 1,
            sheetNameL10n: { values: { en: 'General' } },
            sheetOrdinal: 0,
            tableCode: 'T1',
            tableDefId: 1,
            tableInstanceId: 1,
            tableNameL10n: { values: { en: 'Table 1' } },
            tableOrdinal: 0,
          },
        ]);
      }

      if (url.includes('/api/v1/documents/1')) {
        return jsonResponse({
          businessKey: 'DOC-0001',
          createdAt: '2026-01-01T00:00:00Z',
          id: 1,
          nameL10n: { values: {} },
          projectId: 1,
          sheetCount: 1,
          sheetStates: { GEN: 'Draft' },
        });
      }

      return jsonResponse(null);
    }),
  );
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider>
      <MemoryRouter initialEntries={['/documents/1?periodKey=202401']}>
        <QueryClientProvider client={client}>
          <Routes>
            <Route path="/documents/:id" element={<DocumentPage />} />
          </Routes>
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

/** Mantine у jsdom іде довго — той самий поріг, що в сусідніх наборах сторінки. */
const SlowEnvTimeout = 400_000;
const SettleTimeout = 30_000;

describe('DocumentPage: заповненість видно на сторінці (BE-10)', () => {
  it(
    'показує дріб і крапку помилки',
    async () => {
      mockFetch(() => jsonResponse(Status));
      show();

      /*
       * ⚠ Спершу ЯКІР сторінки за довгим порогом, і лише потім панель — за
       * коротким. Порядок не косметичний: у зворотному вигляді мутація
       * «панель прибрано зі сторінки» падала не з іменем панелі, а
       * `Test timed out in 400000ms` через 6.8 хв (зміряно). Довгий поріг
       * оплачує повільний перший рендер Mantine ОДИН раз і має власне ім'я,
       * а кожне наступне очікування міряє рівно свою обіцянку.
       */
      await screen.findByRole(
        'button',
        { name: '⟦document.validate⟧' },
        { timeout: SlowEnvTimeout },
      );

      const count = await screen.findByTestId('sheet-fill-count', {}, { timeout: SettleTimeout });

      // ⛔ Саме текст, а не сама лише присутність вузла: панель, що показує
      // «0 з 0» на непорожньому документі, — той самий дефект, лише тихіший.
      //
      // ✎ `U-06`: числа тепер їдуть параметрами підпису
      // (`document.tablesFilled`), а не окремим дробом — каталог у тестах
      // порожній, тож `t()` віддає `⟦ключ (filled=1, total=2)⟧`. Ключ
      // перевіряється разом із числами навмисно: підпис, що загубив слова,
      // — це і є вада, заради якої цей рядок змінили.
      const shown = count.textContent?.replace(/\s+/gu, ' ').trim() ?? '';

      expect(shown).toContain('document.tablesFilled');
      expect(shown).toContain('filled=1');
      expect(shown).toContain('total=2');

      expect(screen.queryByTestId('sheet-fill-error-dot')).not.toBeNull();
    },
    SlowEnvTimeout,
  );

  it(
    'відмова сервера прибирає панель, але не сторінку',
    async () => {
      mockFetch(() =>
        jsonResponse(
          // ⚠ `ECR-SYS-0500` — той код, що справді є в каталозі. Вигаданий
          // `ECR-DOC-0500` тут уже стояв, і його спіймав архітектурний сторож
          // `Клієнт_не_згадує_кодів_яких_немає_в_каталозі`: жоден клієнтський
          // файл не має називати коду, якого сервер не віддає.
          { title: 'Server error', status: 500, detail: 'boom', errorCode: 'ECR-SYS-0500' },
          500,
        ),
      );
      show();

      /*
       * ⛔ Доказ того, що сторінка жива, — її власна дія, а не відсутність
       * винятку: `renderFeedback` уже показав, що зламана панель замінює ВЕСЬ
       * маршрут екраном помилки, і тоді «панелі немає» було б правдою через
       * порожній екран.
       *
       * ⚠ Не сітка: з `#299` вона монтується ліниво, за подією спостерігача
       * видимості, а заглушка `IntersectionObserver` у `src/test/setup.ts`
       * інертна — у jsdom немає розкладки. Чекати `grid-stub` тут означало б
       * чекати події, якої ніхто не подасть (зміряно: 400 с таймауту).
       */
      await screen.findByRole(
        'button',
        { name: '⟦document.validate⟧' },
        { timeout: SlowEnvTimeout },
      );

      await waitFor(() => expect(screen.queryByTestId('sheet-fill-summary')).toBeNull(), {
        timeout: SettleTimeout,
      });
    },
    SlowEnvTimeout,
  );
});
