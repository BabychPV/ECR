import type { JSX } from 'react';
import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { DocumentPage } from '@/pages/DocumentPage';

/**
 * Аудит 2026-09-16, §10.7: результат РУЧНОЇ перевірки (`fresh`) переживав
 * зміну документа/періоду.
 *
 * ⛔ Сценарій дефекту: оператор відкриває документ на періоді 202401, тисне
 * «Перевірити» → «3 помилки». Змінює період у заголовку. Компонент НЕ
 * перемонтовується (той самий `<DocumentPage/>` обслуговує кожен
 * `/documents/:id`), `summary`/`tables`/`lastValidation` коректно
 * перезапитуються, — але `fresh` досі тримає старий результат і має
 * ПРІОРИТЕТ над прочитаним (`fresh ?? lastValidation.data`). Отже під
 * періодом, якого ніхто не перевіряв, стоїть перелік чужих зауважень. Це
 * рівно та сама неправда, проти якої застерігає власний коментар коду
 * («зелений напис під документом, якого ніхто не перевіряв»), лише з
 * протилежним знаком.
 *
 * ⚠ Сітка й панель чисел методологій підмінені: вони тягнуть власні запити й
 * рендерять RevoGrid, до якого цей тест не має справи (`D1-12`). Панель
 * зауважень (`ValidationPanel`) — СПРАВЖНЯ: саме її вміст і є доказ.
 */
vi.mock('@/features/grid/DocumentGrid', () => ({
  DocumentGrid: (): JSX.Element => <div data-testid="grid-stub" />,
}));

vi.mock('@/features/methodologies/CalculationResultsPanel', () => ({
  CalculationResultsPanel: (): JSX.Element => <div data-testid="calc-stub" />,
}));

/** Текст зауваження, яке повертає перевірка періоду 202401 — і лише його. */
const Message202401 = 'Рядок R1: колонка C1 обов’язкова (період 202401)';

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

function mockFetch(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
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

      // ⚠ Ручна перевірка відповідає ЗА ПЕРІОД ІЗ ТІЛА запиту — так само, як
      // сервер після `A7-28`.
      if (url.includes('/validate')) {
        const body = JSON.parse(String(init?.body ?? '{}')) as { periodKey?: number };

        return jsonResponse({
          documentId: 1,
          periodKey: body.periodKey ?? 0,
          messages: [
            {
              severity: 'Error',
              // ⚠ Код ПРАВИЛА, не код помилки: `ValidationMessage.RuleCode` —
              // це `ValidationRule.Code` з конфігурації (`CAP`, `BALANCE`), і
              // сторож `Клієнт_не_згадує_кодів_яких_немає_в_каталозі`
              // (Ecr.Architecture.Tests) слушно ловить тут вигаданий `ECR-*`.
              ruleCode: 'BALANCE',
              rowKey: 'R1',
              columnCode: 'C1',
              message: Message202401,
            },
          ],
        });
      }

      // ⛔ «Ще не перевіряли» — це `404`, а не порожній перелік: інакше
      // зникнення `fresh` неможливо відрізнити від «перевірили, зауважень
      // немає».
      if (url.includes('/validation')) {
        return jsonResponse(
          // ⚠ Саме `ECR-DOC-0404` — той код, що його справді віддає
          // `DocumentsController.LastValidation`; вигаданий `ECR-VAL-0404`
          // ловить архітектурний сторож каталогу кодів.
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
          {/* ⚠ Саме через `Routes`: `DocumentPage` читає `id` з `useParams()`,
              і без оголошеного шаблону маршруту документ був би `NaN`. */}
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

/**
 * Mantine у jsdom іде довго (`D1-12`) — той самий поріг, що в
 * `DocumentsPage.periodFilter`, але лише на ПЕРШИЙ рендер сторінки.
 *
 * ⚠ Переходи стану перевіряються коротшим порогом: `waitFor` на умову, яка НЕ
 * настане (саме так виглядає RED цього тесту), висить рівно свій ліміт — і на
 * 400 000 мс один прогін коштував би тринадцять хвилин очікування нічого.
 */
const SlowEnvTimeout = 400_000;
const SettleTimeout = 30_000;

describe('DocumentPage: результат перевірки не переживає зміну періоду (§10.7)', () => {
  it(
    'зауваження періоду 202401 зникають при переході на 202402 і повертаються після нової перевірки',
    async () => {
      mockFetch();
      show();

      fireEvent.click(
        await screen.findByRole(
          'button',
          { name: '⟦document.validate⟧' },
          { timeout: SlowEnvTimeout },
        ),
      );

      // Перевірка 202401 дала зауваження — вони на екрані.
      await waitFor(() => expect(screen.getByText(Message202401)).toBeTruthy(), {
        timeout: SettleTimeout,
      });

      // Оператор змінює період у заголовку — компонент НЕ перемонтовується.
      fireEvent.change(await screen.findByLabelText('⟦documents.period⟧'), {
        target: { value: '202402' },
      });

      /*
       * ⛔ Обидва очікування обов'язкові, і перше — не формальність. Новий
       * період — це НОВІ ключі кешу, тож `AsyncBoundary` на мить показує
       * скелет (`role="status"`), і в цю мить панелі зауважень немає ПРОСТО
       * ТОМУ, що немає нічого. Перевіряти відсутність тексту саме тоді
       * означало б написати тест, зелений і БЕЗ фіксу. Тому спершу чекаємо,
       * що перезавантаження почалося, потім — що воно скінчилося, і лише на
       * повністю перемальованій сторінці 202402 дивимось, що на ній.
       */
      await waitFor(() => expect(screen.queryByRole('status')).not.toBeNull(), {
        timeout: SettleTimeout,
      });
      await waitFor(() => expect(screen.queryByRole('status')).toBeNull(), {
        timeout: SettleTimeout,
      });

      const validateAgain = screen.getByRole('button', { name: '⟦document.validate⟧' });

      // ⛔ Мутаційний доказ (RED до фіксу): `fresh` лишався непорушним і мав
      // пріоритет над `lastValidation.data`, тож ЦЕЙ САМЕ текст — зауваження
      // періоду 202401 — і далі стояв під періодом 202402, який ніхто не
      // перевіряв.
      expect(screen.queryByText(Message202401)).toBeNull();

      // ⚠ І не підміняється на «зауважень немає»: `404` означає «не
      // перевіряли», і панель зникає ЦІЛКОМ, а не зеленіє.
      expect(screen.queryByText('⟦document.validationCleanHint⟧')).toBeNull();

      // ⚠ Скидання не має ЗАМИКАТИ панель назавжди: повторна перевірка вже на
      // новому періоді показує свій результат так само, як перша.
      fireEvent.click(validateAgain);

      await waitFor(() => expect(screen.getByText(Message202401)).toBeTruthy(), {
        timeout: SettleTimeout,
      });
    },
    SlowEnvTimeout,
  );
});
