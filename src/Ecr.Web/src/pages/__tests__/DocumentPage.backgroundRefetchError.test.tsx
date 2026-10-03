import type { JSX } from 'react';
import { describe, it, expect, vi, afterEach } from 'vitest';
import { act, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { DocumentPage } from '@/pages/DocumentPage';

/**
 * AN-39 / L8-11: збій ФОНОВОГО перезапиту (в кеші вже є дані) підміняв усю сторінку
 * документа на ErrorAlert - розмонтовувались сітки, редактор, Undo, панель конфлікту.
 * Тепер сторінка лишається, помилка - банер зверху.
 */
vi.mock('@/features/grid/DocumentGrid', () => ({
  DocumentGrid: (): JSX.Element => <div data-testid="grid-stub" />,
}));

vi.mock('@/features/methodologies/CalculationResultsPanel', () => ({
  CalculationResultsPanel: (): JSX.Element => <div data-testid="calc-stub" />,
}));

let tablesFail = false;

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json' },
  });
}

function mockFetch(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('/api/v1/me')) {
        return jsonResponse({ denies: [], grants: {}, isSimulation: false, language: 'en', mustChangePassword: false, permissions: [], simulatedForUserId: null, userId: 1, userName: 'tester' });
      }

      if (url.includes('/tables/status')) return jsonResponse([]);

      if (url.includes('/validation')) {
        return jsonResponse({ title: 'Not found', status: 404, detail: 'not validated', errorCode: 'ECR-DOC-0404' }, 404);
      }

      if (url.includes('/tables')) {
        if (tablesFail) {
          return jsonResponse({ title: 'Server error', status: 500, detail: 'boom', errorCode: 'ECR-SYS-0500' }, 500);
        }

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

afterEach(() => {
  vi.unstubAllGlobals();
  tablesFail = false;
});

const SlowEnvTimeout = 400_000;
const SettleTimeout = 30_000;

describe('DocumentPage: фоновий збій перезапиту (L8-11)', () => {
  it(
    'сторінка лишається, помилка - банер; кнопка дій не зникає',
    async () => {
      mockFetch();
      tablesFail = false;
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

      await screen.findByRole('button', { name: '⟦document.validate⟧' }, { timeout: SlowEnvTimeout });

      // Фоновий перезапит упав.
      tablesFail = true;
      await act(async () => {
        await client.refetchQueries({ queryKey: ['document-tables'] });
      });

      await waitFor(() => expect(screen.queryByText(/ECR-SYS-0500/)).not.toBeNull(), { timeout: SettleTimeout });

      // ⛔ Сторінка не підмінена помилкою: її власна дія на місці.
      expect(screen.queryByRole('button', { name: '⟦document.validate⟧' })).not.toBeNull();
    },
    SlowEnvTimeout,
  );
});
