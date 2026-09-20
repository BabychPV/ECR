import type { JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { DocumentPage } from '@/pages/DocumentPage';
import { testTheme } from '@/test/render';

/**
 * Директива №15 §0, правило `L10`: **відмова ≠ порожньо**.
 *
 * ⛔ Сценарій дефекту. `GET /documents/{id}/validation` читає збережений
 * підсумок останньої перевірки, і клієнт розрізняв рівно ОДИН випадок —
 * `404` = «ще не перевіряли». Але `403` (немає права на читання підсумку),
 * `500` і обрив мережі дають те саме `data === undefined`, тобто
 * `shownValidation === null`, тобто `ValidationPanel` не малюється ЗОВСІМ.
 * Документ, у якому на сервері вже лежать БЛОКУВАЛЬНІ помилки, виглядав
 * рівно як неперевірений і чистий: оператор тиснув «Подати» й діставав
 * відмову `ECR-SUB-*`, не маючи на екрані жодного натяку чому.
 *
 * ⚠ Це не «не показали даних», а «показали неправду про готовність» — той
 * самий дефект, проти якого застерігає власний коментар коду («зелений напис
 * під документом, якого ніхто не перевіряв»), лише з протилежним знаком.
 *
 * ⚠ Три випадки нижче — доказ у зборі, і дзеркало обов'язкове: «полагодити»
 * це можна було б банером на КОЖНОМУ документі, якого ніхто не перевіряв, і
 * випадок Б такий фікс валить.
 */

/*
 * ⚠ Підмінені лише важкі діти, і з названої причини: `SheetTables` тягне ядро
 * `RevoGrid`, `CalculationResultsPanel` — власні запити й числа методологій. У
 * jsdom це хвилини на рендер, а предмет перевірки — що сторінка каже про стан
 * ПЕРЕВІРКИ, а не як малюється сітка. `ValidationPanel` і `ErrorAlert` —
 * СПРАВЖНІ: саме вони і є доказ.
 */
vi.mock('@/features/grid/SheetTables', () => ({
  SheetTables: (): JSX.Element => <div data-testid="grid-stub" />,
}));

vi.mock('@/features/methodologies/CalculationResultsPanel', () => ({
  CalculationResultsPanel: (): JSX.Element => <div data-testid="calc-stub" />,
}));

/** Період в адресі сторінки — він же частина ключа кеша перевірки. */
const PeriodKey = 202401;

/** Текст, який сервер віддає у відмові читання підсумку. */
const ServerDetail = 'підсумок перевірки прочитати не вдалося';

/** Текст збереженого зауваження — випадок «усе гаразд, панель на місці». */
const SavedFinding = 'Рядок R1: колонка C1 обов’язкова';

/** Чим відповідає `GET …/validation`. */
type ValidationMode = 'error' | 'notValidated' | 'saved';

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

/**
 * Заглушка сервера.
 *
 * ⚠ Маршрути звіряються за ШЛЯХОМ без рядка запиту і на ПОВНЕ співпадіння:
 * `includes('/api/v1/me')` збігається і з `/api/v1/methodologies`, а
 * `includes('/api/v1/documents/1')` — з `/api/v1/documents/1/validation`.
 */
function mockServer(mode: ValidationMode): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const path = String(input).split('?')[0] ?? '';

      if (path === '/api/v1/me') {
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

      if (path === '/api/v1/documents/1/validation') {
        if (mode === 'error') {
          return jsonResponse(
            {
              title: 'Internal error',
              status: 500,
              detail: ServerDetail,
              errorCode: 'ECR-SYS-0500',
              correlationId: 'corr-validation-1',
              // ⚠ БЕЗ `messageKey` подробиця на екран не потрапляє взагалі
              // (`problemText.mayShowDetail`), і тест перевіряв би сам лише
              // заголовок. 500-та — серед шляхів, які сервер уже позначає.
              messageKey: 'err.ECR-SYS-0500.unexpected',
            },
            500,
          );
        }

        if (mode === 'notValidated') {
          // ⚠ Саме `ECR-DOC-0404` — код, який віддає
          // `DocumentsController.LastValidation`.
          return jsonResponse(
            {
              title: 'Not found',
              status: 404,
              detail: 'not validated',
              errorCode: 'ECR-DOC-0404',
              correlationId: 'corr-validation-2',
            },
            404,
          );
        }

        return jsonResponse({
          documentId: 1,
          periodKey: PeriodKey,
          messages: [
            {
              severity: 'Error',
              // ⚠ Код ПРАВИЛА, не код помилки: сторож каталогу кодів слушно
              // ловить тут вигаданий `ECR-*`.
              ruleCode: 'BALANCE',
              tableDefId: 1,
              rowKey: 'R1',
              columnCode: 'C1',
              message: SavedFinding,
            },
          ],
        });
      }

      // ⚠ Заповненість («68 / 91») поруч із панеллю: без цього маршруту
      // `SheetFillSummary` падає на `null` і зносить сторінку цілком — тобто
      // тест перевіряв би не те, що написано.
      if (path === '/api/v1/documents/1/tables/status') {
        return jsonResponse([
          { tableInstanceId: 1, filledCells: 1, inputCells: 1, errorCount: null },
        ]);
      }

      if (path === '/api/v1/documents/1/tables') {
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

      if (path === '/api/v1/documents/1') {
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

/** Рендерить сторінку і віддає клієнт запитів — за ним видно, коли відповідь прийшла. */
function show(): QueryClient {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter initialEntries={[`/documents/1?periodKey=${String(PeriodKey)}`]}>
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

  return client;
}

/**
 * Стеля самого ТЕСТУ — та сама, що в сусідніх тестах сторінки (`D1-12`:
 * Mantine у jsdom іде довго).
 *
 * ⛔ А ось усі `waitFor`/`findBy` всередині — 30 с, і це не стиль. Очікування
 * на умову, яка НЕ настане, — саме так виглядає RED цього тесту — висить рівно
 * свій ліміт: на 400 000 мс мутаційний прогін коштував би сім хвилин
 * очікування нічого, ще й із марним «Test timed out» замість «Unable to find
 * …alert». Виміряно тут: усі три випадки разом ідуть 14 с, тобто запас
 * шестикратний.
 */
const SlowEnvTimeout = 400_000;
const SettleTimeout = 30_000;

/**
 * Чекає, поки запит підсумку перевірки ВІДПОВІВ.
 *
 * ⛔ Без цього дзеркальні твердження («банера немає») були б зелені просто
 * тому, що відповідь ще в дорозі, — тобто зелені й БЕЗ фіксу. Стан запиту в
 * кеші — єдиний спосіб побачити прихід відмови, коли вона нічого не малює.
 */
async function awaitValidationSettled(
  client: QueryClient,
  status: 'error' | 'success',
): Promise<void> {
  await waitFor(
    () => expect(client.getQueryState(['validation', 1, PeriodKey])?.status).toBe(status),
    { timeout: SettleTimeout },
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('DocumentPage: невдале читання підсумку перевірки ≠ «зауважень немає» (L10)', () => {
  it(
    'А. 500 від сервера показує причину з кодом, а не порожнє місце',
    async () => {
      mockServer('error');
      show();

      // ⛔ Мутаційний доказ (RED до фіксу): `shownValidation` ставав `null`,
      // панель не малювалася, і на екрані НЕ БУЛО нічого з `role="alert"` —
      // жодного натяку, що підсумок прочитати не вдалося.
      const alert = await screen.findByRole('alert', {}, { timeout: SettleTimeout });

      expect(alert.textContent ?? '').toContain(ServerDetail);
      expect(alert.textContent ?? '').toContain('ECR-SYS-0500');
      expect(alert.textContent ?? '').toContain('corr-validation-1');

      // ⛔ Тупикових екранів не буває (`ФВ-14.24`): повторити читання можна
      // звідси, а не перезавантаженням сторінки.
      expect(
        within(alert).getByRole('button', { name: '⟦common.retry⟧' }),
      ).toBeTruthy();

      // ⚠ І екран не стверджує протилежного: зеленого «перевірено, зауважень
      // немає» тут бути не може — ніхто нічого не прочитав.
      expect(screen.queryByText('⟦document.validationCleanHint⟧')).toBeNull();
    },
    SlowEnvTimeout,
  );

  it(
    'Б. ДЗЕРКАЛО: 404 і далі означає «ще не перевіряли» — жодного банера',
    async () => {
      mockServer('notValidated');
      const client = show();

      // Спершу — що відмова СПРАВДІ прийшла, і лише потім питання про екран.
      await awaitValidationSettled(client, 'error');

      // ⛔ Мутаційний доказ: варто зняти виняток для `404` — і банер стане під
      // кожним документом, якого ніхто не перевіряв. Панель при цьому
      // лишається відсутньою (а не зеленою): «не перевіряли» — не «чисто».
      expect(screen.queryByRole('alert')).toBeNull();
      expect(screen.queryByText('⟦document.validationCleanHint⟧')).toBeNull();
      expect(screen.queryByRole('button', { name: '⟦common.retry⟧' })).toBeNull();

      // ⚠ Сторінка при цьому змальована, а не «ще вантажиться»: інакше
      // відсутність банера нічого не доводила б.
      expect(
        screen.getByRole('button', { name: '⟦document.validate⟧' }),
      ).toBeTruthy();
    },
    SlowEnvTimeout,
  );

  it(
    'В. Звичайна відповідь 200 показує панель зауважень і жодного банера',
    async () => {
      mockServer('saved');
      const client = show();

      await awaitValidationSettled(client, 'success');

      const finding = await screen.findByText(SavedFinding, undefined, {
        timeout: SettleTimeout,
      });

      // ⚠ Панель — це `Alert` Mantine, а той теж має `role="alert"`, тож
      // «банера немає» перевіряється НЕ відсутністю ролі, а відсутністю того,
      // що є лише в банері: дії «повторити» й коду відмови.
      expect(finding).toBeTruthy();
      expect(screen.queryByRole('button', { name: '⟦common.retry⟧' })).toBeNull();
      expect(screen.queryByText(/ECR-SYS-0500/)).toBeNull();
    },
    SlowEnvTimeout,
  );
});
