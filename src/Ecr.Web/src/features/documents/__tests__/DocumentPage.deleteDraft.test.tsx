import type { JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { DocumentPage } from '@/pages/DocumentPage';

/**
 * Кнопка «Видалити» підключена до СТОРІНКИ документа — і лише там, де треба.
 *
 * ⚠ Логіку показу перевіряє `DeleteDocumentAction.test.tsx`; тут — що сторінка
 * передає хуку справжні право сесії й стани аркушів з `GET /documents/{id}`, а
 * не, скажімо, стан лише активного аркуша чи право іншої дії.
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

function mockFetch(permissions: string[], sheetStates: Record<string, string>): void {
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

      if (url.includes('/tables/status')) return jsonResponse([]);

      if (url.includes('/tables')) {
        return jsonResponse(
          ['GEN', 'AIR'].map((code, index) => ({
            allowsDynamicRows: false,
            maxDynamicRows: null,
            sheetCode: code,
            sheetDefId: index + 1,
            sheetNameL10n: { values: { en: code } },
            sheetOrdinal: index,
            tableCode: `T${String(index)}`,
            tableDefId: index + 1,
            tableInstanceId: index + 1,
            tableNameL10n: { values: { en: `Table ${String(index)}` } },
            tableOrdinal: 0,
          })),
        );
      }

      if (url.includes('/api/v1/documents/1')) {
        return jsonResponse({
          businessKey: 'DOC-0001',
          createdAt: '2026-01-01T00:00:00Z',
          id: 1,
          nameL10n: { values: {} },
          projectId: 1,
          sheetCount: 2,
          sheetStates,
        });
      }

      return jsonResponse(null);
    }),
  );
}

function show(permissions: string[], sheetStates: Record<string, string>): void {
  mockFetch(permissions, sheetStates);

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

const SlowEnvTimeout = 400_000;
const DeleteButton = { name: '⟦documents.delete⟧' };
const ValidateButton = { name: '⟦document.validate⟧' };

describe('DocumentPage: кнопка «Видалити документ-чернетку»', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it(
    'право Document.Delete і всі аркуші Draft — кнопка в шапці є',
    async () => {
      show(['Document.View', 'Document.Delete'], { GEN: 'Draft', AIR: 'Draft' });

      expect(
        await screen.findByRole('button', DeleteButton, { timeout: SlowEnvTimeout }),
      ).toBeDefined();
    },
    SlowEnvTimeout,
  );

  it(
    'без права Document.Delete — кнопки немає',
    async () => {
      show(['Document.View'], { GEN: 'Draft', AIR: 'Draft' });

      // ⛔ Спершу — що сторінка намальована, інакше «кнопки немає» було б
      // правдою просто тому, що не намальовано нічого.
      await screen.findByRole('button', ValidateButton, { timeout: SlowEnvTimeout });
      expect(screen.queryByRole('button', DeleteButton)).toBeNull();
    },
    SlowEnvTimeout,
  );

  it(
    'не чернетка: НЕАКТИВНИЙ аркуш поданий — кнопки немає',
    async () => {
      // ⚠ Поданий саме другий аркуш, а активний (перший) — `Draft`: сторінка
      // мусить питати про ВЕСЬ документ, а не про вкладку, що відкрита.
      show(['Document.View', 'Document.Delete'], { GEN: 'Draft', AIR: 'Submitted' });

      await screen.findByRole('button', ValidateButton, { timeout: SlowEnvTimeout });
      expect(screen.queryByRole('button', DeleteButton)).toBeNull();
    },
    SlowEnvTimeout,
  );
});
