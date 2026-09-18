import type { JSX } from 'react';
import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { DocumentPage } from '@/pages/DocumentPage';

/**
 * Панель чисел методологій — під правом `Calculation.View`.
 *
 * ⛔ ЗНАЙДЕНО ПРОХОДОМ ІНТЕРФЕЙСУ ЯК КОРИСТУВАЧ (`docs/build/UI-WALKTHROUGH.md`,
 * F2), і жоден компонентний тест цього не бачив ЗА ПОБУДОВОЮ: кожен із них
 * підміняє `CalculationResultsPanel` заглушкою, тобто перевіряє сторінку
 * НАВКОЛО панелі, а не те, чи її взагалі слід малювати.
 *
 * ⛔ Суть дефекту. Панель стояла в розмітці БЕЗУМОВНО, тоді як сусідні дії
 * (`ImportPanel`, `ExportPanel`) там-таки закриті через `can(...)`. Оператор
 * без `Calculation.View` відкривав ВЛАСНИЙ документ і бачив унизу плашку
 * `403 Calculation.View` — там, де решта недоступного просто не малюється.
 * Відмова сервера була правильною; неправильним було те, що її питали.
 *
 * ⚠ Заглушка панелі стоїть і тут, але предмет перевірки інший: не її вміст, а
 * сама її ПРИСУТНІСТЬ залежно від прав.
 */
vi.mock('@/features/grid/DocumentGrid', () => ({
  DocumentGrid: (): JSX.Element => <div data-testid="grid-stub" />,
}));

vi.mock('@/features/methodologies/CalculationResultsPanel', () => ({
  CalculationResultsPanel: (): JSX.Element => <div data-testid="calc-stub" />,
}));

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

function mockFetch(permissions: string[]): void {
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
          permissions,
          simulatedForUserId: null,
          userId: 1,
          userName: 'tester',
        });
      }

      // «Ще не перевіряли» — саме `404`, як у `DocumentsController.LastValidation`.
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

/**
 * ⚠ Маршрут оголошено саме як `/documents/:id`: `DocumentPage` читає `id` з
 * `useParams()`, і під іншим іменем параметра документ був би `NaN` — запити
 * пішли б на `/api/v1/documents/NaN`, сторінка впала б на `sheetStates`
 * порожнього тіла, і «панелі немає» стало б правдою з геть іншої причини.
 */
function show(permissions: string[]): void {
  mockFetch(permissions);

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

/** Mantine у jsdom іде довго (`D1-12`) — той самий поріг, що в сусідніх тестах. */
const SlowEnvTimeout = 400_000;

describe('DocumentPage: панель чисел методологій під правом (UI-WALKTHROUGH F2)', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it(
    'без Calculation.View панелі НЕМАЄ — оператор не бачить 403 на власному документі',
    async () => {
      show(['Document.View']);

      // ⛔ Спершу дочікуємося, що сторінка справді намальована: інакше
      // «панелі немає» було б зелено просто тому, що не намальовано нічого —
      // тест, який пройшов би і БЕЗ фіксу.
      await screen.findByRole(
        'button',
        { name: '⟦document.validate⟧' },
        { timeout: SlowEnvTimeout },
      );

      // ⛔ Мутаційний доказ (RED до фіксу): панель стояла безумовно, тож
      // заглушка була присутня й без права.
      expect(screen.queryByTestId('calc-stub')).toBeNull();
    },
    SlowEnvTimeout,
  );

  it(
    'з Calculation.View панель є — право не ховає її від того, кому вона належить',
    async () => {
      show(['Document.View', 'Calculation.View']);

      await waitFor(() => expect(screen.queryByTestId('calc-stub')).not.toBeNull(), {
        timeout: SlowEnvTimeout,
      });
    },
    SlowEnvTimeout,
  );
});
