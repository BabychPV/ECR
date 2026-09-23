import type { JSX } from 'react';
import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

/**
 * `ImportPanel` і `CalculationResultsPanel` — за `import()`, не статичним
 * імпортом (`D-132`, перф-рефакторинг «bundle splitting»).
 *
 * ⚠ Чим це ловиться, так само як `features/search/__tests__/
 * SearchLauncher.lazy.test.tsx`: фабрика `vi.mock` виконується, коли модуль
 * ІМПОРТУЮТЬ уперше. Статичний `import { ImportPanel } from '...'` у
 * `DocumentPage.tsx` обчислив би модуль одразу при монтуванні сторінки, ще до
 * того, як права/стан документа взагалі відомі, — і перша перевірка нижче
 * (без права/на непередагованому документі) уже застала б `evaluated: true`.
 * Лінивий `import()` обчислює модуль лише тоді, коли умова показу справдилась.
 *
 * ⛔ `DocumentGrid` замінено заглушкою навмисно (як і в сусідніх тестах
 * сторінки): справжній модуль тягне `RevoGrid`, і в цьому тесті перевіряється
 * не сітка, а сусідні панелі.
 */
const probe = vi.hoisted(() => ({ importEvaluated: false, calcEvaluated: false }));

vi.mock('@/features/grid/DocumentGrid', () => ({
  DocumentGrid: (): JSX.Element => <div data-testid="grid-stub" />,
}));

vi.mock('@/features/import/ImportPanel', () => {
  probe.importEvaluated = true;

  return {
    ImportPanel: (): JSX.Element => <div data-testid="import-stub" />,
  };
});

vi.mock('@/features/methodologies/CalculationResultsPanel', () => {
  probe.calcEvaluated = true;

  return {
    CalculationResultsPanel: (): JSX.Element => <div data-testid="calc-stub" />,
  };
});

import { DocumentPage } from '@/pages/DocumentPage';

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

function mockFetch(permissions: string[], sheetState = 'Draft'): void {
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
          sheetStates: { GEN: sheetState },
        });
      }

      return jsonResponse(null);
    }),
  );
}

function show(permissions: string[], sheetState = 'Draft'): void {
  mockFetch(permissions, sheetState);

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

describe('DocumentPage: ImportPanel/CalculationResultsPanel лінивi (D-132)', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it(
    'без прав на імпорт/розрахунок — жоден із двох модулів не обчислено',
    async () => {
      show(['Document.View']);

      // ⛔ Спершу дочікуємося, що сторінка справді намальована: інакше
      // «модуль не обчислено» було б зелено просто тому, що не намальовано
      // нічого.
      await screen.findByRole(
        'button',
        { name: '⟦document.validate⟧' },
        { timeout: SlowEnvTimeout },
      );

      expect(screen.queryByTestId('import-stub')).toBeNull();
      expect(screen.queryByTestId('calc-stub')).toBeNull();
      expect(probe.importEvaluated).toBe(false);
      expect(probe.calcEvaluated).toBe(false);
    },
    SlowEnvTimeout,
  );

  it(
    'з правом Calculation.View — панель з’являється, і лише ЇЇ модуль довантажується',
    async () => {
      show(['Document.View', 'Calculation.View']);

      await waitFor(() => expect(screen.queryByTestId('calc-stub')).not.toBeNull(), {
        timeout: SlowEnvTimeout,
      });

      expect(probe.calcEvaluated).toBe(true);
      // Право на імпорт відсутнє — цей модуль лишається недовантаженим.
      expect(screen.queryByTestId('import-stub')).toBeNull();
      expect(probe.importEvaluated).toBe(false);
    },
    SlowEnvTimeout,
  );

  it(
    'з правом Document.Import на редагованому аркуші — панель імпорту довантажується',
    async () => {
      show(['Document.View', 'Document.Import'], 'Draft');

      await waitFor(() => expect(screen.queryByTestId('import-stub')).not.toBeNull(), {
        timeout: SlowEnvTimeout,
      });

      expect(probe.importEvaluated).toBe(true);
    },
    SlowEnvTimeout,
  );
});
