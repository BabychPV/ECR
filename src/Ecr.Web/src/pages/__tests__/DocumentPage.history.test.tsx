import type { JSX } from 'react';
import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { DocumentPage } from '@/pages/DocumentPage';

/**
 * `BE-11b`: блок «History» СТОЇТЬ НА СТОРІНЦІ згорнутим і запиту не робить.
 *
 * ⚠ Поведінку самого блока доводить `features/workflow/__tests__/
 * WorkflowHistory.test.tsx`; той набір лишився б зеленим, якби блок прибрали зі
 * сторінки або розгорнули за замовчуванням саме тут.
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

const requested: string[] = [];

function mockFetch(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      requested.push(url);

      if (url.includes('/api/v1/me')) {
        return jsonResponse({
          denies: [], grants: {}, isSimulation: false, language: 'en', mustChangePassword: false,
          permissions: [], simulatedForUserId: null, userId: 1, userName: 'tester',
        });
      }

      if (url.includes('/tables/status')) return jsonResponse([]);

      if (url.includes('/validation')) {
        return jsonResponse({ title: 'Not found', status: 404, errorCode: 'ECR-DOC-0404' }, 404);
      }

      if (url.includes('/tables')) {
        return jsonResponse([
          {
            allowsDynamicRows: false, maxDynamicRows: null, sheetCode: 'GEN', sheetDefId: 1,
            sheetNameL10n: { values: { en: 'General' } }, sheetOrdinal: 0, tableCode: 'T1',
            tableDefId: 1, tableInstanceId: 1, tableNameL10n: { values: { en: 'Table 1' } },
            tableOrdinal: 0,
          },
        ]);
      }

      return jsonResponse({
        businessKey: 'DOC-0001', createdAt: '2026-01-01T00:00:00Z', id: 1, nameL10n: { values: {} },
        projectId: 1, sheetCount: 1, sheetStates: { GEN: 'Submitted' },
      });
    }),
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

/** Mantine у jsdom іде довго — той самий поріг, що в сусідніх наборах сторінки. */
const SlowEnvTimeout = 400_000;

describe('DocumentPage: історія погоджень (BE-11b)', () => {
  it(
    'кнопка історії є на сторінці, згорнута, і запит історії не йде',
    async () => {
      mockFetch();

      render(
        <MantineProvider>
          <MemoryRouter initialEntries={['/documents/1?periodKey=202401']}>
            <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
              <Routes>
                <Route path="/documents/:id" element={<DocumentPage />} />
              </Routes>
            </QueryClientProvider>
          </MemoryRouter>
        </MantineProvider>,
      );

      const toggle = await screen.findByRole(
        'button',
        { name: '⟦workflow.history⟧' },
        { timeout: SlowEnvTimeout },
      );

      expect(toggle.getAttribute('aria-expanded')).toBe('false');
      expect(requested.filter((url) => url.includes('/workflow/history'))).toEqual([]);
    },
    SlowEnvTimeout,
  );
});
