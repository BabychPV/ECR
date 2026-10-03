import type { JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { DocumentPage } from '@/pages/DocumentPage';
import { testTheme } from '@/test/render';

/**
 * T2-08: бейдж стану на вкладці аркуша показував сирий `DRAFT`/`SUBMITTED` навіть у ru/kz, хоч решта
 * екрана перекладена. Тепер — той самий `StatusBadge`, що й у списку документів (підпис із каталогу).
 */

vi.mock('@/features/methodologies/CalculationResultsPanel', () => ({
  CalculationResultsPanel: (): JSX.Element => <div data-testid="calc-stub" />,
}));

vi.mock('@/features/import/ImportPanel', () => ({
  ImportPanel: (): JSX.Element => <button type="button">import-stub</button>,
}));

const SlowEnvTimeout = 400_000;
const Period = 202401;

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function mockFetch(sheetState: string): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('/api/v1/me')) {
        return jsonResponse({
          denies: [],
          grants: { 'Project:1': 'Manage' },
          isSimulation: false,
          language: 'en',
          mustChangePassword: false,
          permissions: ['Document.View'],
          simulatedForUserId: null,
          userId: 1,
          userName: 'tester',
        });
      }

      if (url.includes('/api/v1/projects?')) {
        return jsonResponse({ items: [], nextCursor: null, totalCount: 0 });
      }

      if (url.includes('/api/v1/projects/1/periods')) {
        return jsonResponse({
          projectId: 1,
          periodKind: 'Monthly',
          currentPeriodMode: 'Auto',
          timeZoneId: 'UTC',
          policy: {},
          periods: [],
        });
      }

      if (url.includes('/validation')) return jsonResponse({ documentId: 1, periodKey: Period, messages: [], validated: false });
      if (url.includes('/tables/status')) return jsonResponse([]);

      if (url.includes('/tables')) {
        return jsonResponse([
          {
            allowsDynamicRows: false,
            maxDynamicRows: null,
            sheetCode: 'GEN',
            sheetDefId: 1,
            sheetNameL10n: { values: { en: 'General' } },
            sheetOrdinal: 0,
            tableCode: 'T0',
            tableDefId: 1,
            tableInstanceId: 1,
            tableNameL10n: { values: { en: 'Table 0' } },
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
          hasLateEdits: false,
        });
      }

      return jsonResponse(null);
    }),
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('DocumentPage: стан аркуша на вкладці', () => {
  it.each(['Draft', 'Submitted'])(
    'стан %s — підпис із каталогу, а не сирий код',
    async (state) => {
      mockFetch(state);
      const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

      render(
        <MantineProvider theme={testTheme}>
          <MemoryRouter initialEntries={[`/documents/1?periodKey=${String(Period)}`]}>
            <QueryClientProvider client={client}>
              <Routes>
                <Route path="/documents/:id" element={<DocumentPage />} />
              </Routes>
            </QueryClientProvider>
          </MemoryRouter>
        </MantineProvider>,
      );

      const tab = await screen.findByRole('tab', { name: /General/ }, { timeout: SlowEnvTimeout });
      const badge = tab.querySelector('[data-status-kind="sheet"]');

      expect(badge).not.toBeNull();
      // У тестовому середовищі підпис — ⟦ключ⟧; сирий `Draft`/`DRAFT` без обгортки ключа — дефект.
      expect(tab.textContent).toContain(`⟦status.sheet.${state}⟧`);
      expect(tab.textContent?.replace(`⟦status.sheet.${state}⟧`, '')).not.toMatch(new RegExp(state, 'i'));
    },
    SlowEnvTimeout,
  );
});
