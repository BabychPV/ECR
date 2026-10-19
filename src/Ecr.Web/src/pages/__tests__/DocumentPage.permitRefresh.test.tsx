import type { JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { DocumentPage } from '@/pages/DocumentPage';
import { testTheme } from '@/test/render';

/**
 * «З дозволу» + «Оновити з дозволу» в сторінці документа (Permits, PM-11): смуга з'являється для таблиці
 * активного аркуша з прив'язкою Rows і лише з правом `Template.View`; кнопка шле POST за кодом прив'язки.
 *
 * Мутаційний доказ (виконано, відновлено): у `DocumentPage` прибрати `can(session.data, 'Template.View') &&`
 * валить «без Template.View»; замінити `tables={active.tables}` на `tables={[]}` валить «показує».
 */
vi.mock('@/features/methodologies/CalculationResultsPanel', () => ({
  CalculationResultsPanel: (): JSX.Element => <div data-testid="calc-stub" />,
}));
vi.mock('@/features/import/ImportPanel', () => ({
  ImportPanel: (): JSX.Element => <button type="button">import-stub</button>,
}));

const SlowEnvTimeout = 400_000;
const Period = 202401;
const jsonResponse = (body: unknown, status = 200): Response =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });

const calls: { url: string; method: string }[] = [];

function mockFetch(permissions: string[]): void {
  calls.length = 0;
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      calls.push({ url, method: init?.method ?? 'GET' });

      if (url.includes('/api/v1/me')) {
        return jsonResponse({ denies: [], grants: { 'Project:1': 'Manage' }, isSimulation: false, language: 'en', mustChangePassword: false, permissions, simulatedForUserId: null, userId: 1, userName: 'tester' });
      }
      if (url.includes('/permit-bindings/PB1_T0/refresh')) {
        return jsonResponse({ added: 3, updated: 0, removedFromPermit: [], warnings: [] });
      }
      if (url.includes('/permit-bindings')) {
        return jsonResponse({
          isEditable: true,
          items: [
            {
              id: 1, code: 'PB1_T0', sheetTableDefId: 1, sheetTableCode: 'T0', permitTypeCode: 'AirPermit', permitTableCode: 'AirEmissions',
              mode: 'Rows', selectionRule: 'ByFacilityPeriodEnd', headerFieldDefId: null, headerFieldCode: null, isActive: true, rowVersion: 'x', columns: [],
            },
          ],
        });
      }
      if (url.includes('/document-template')) return jsonResponse({ templateVersionId: 7, templateCode: 'GEN', groupRules: [], sheets: [] });
      if (url.includes('/api/v1/projects?')) return jsonResponse({ items: [], nextCursor: null, totalCount: 0 });
      if (url.includes('/api/v1/projects/1/periods')) {
        return jsonResponse({ projectId: 1, periodKind: 'Monthly', currentPeriodMode: 'Auto', timeZoneId: 'UTC', policy: {}, periods: [] });
      }
      if (url.includes('/validation')) return jsonResponse({ documentId: 1, periodKey: Period, messages: [], validated: false });
      if (url.includes('/tables/status')) return jsonResponse([]);
      if (url.includes('/tables')) {
        return jsonResponse([
          { allowsDynamicRows: true, maxDynamicRows: 100, sheetCode: 'GEN', sheetDefId: 1, sheetNameL10n: { values: { en: 'General' } }, sheetOrdinal: 0, tableCode: 'T0', tableDefId: 1, tableInstanceId: 1, tableNameL10n: { values: { en: 'Table 0' } }, tableOrdinal: 0 },
        ]);
      }
      if (url.includes('/api/v1/documents/1')) {
        return jsonResponse({ businessKey: 'DOC-0001', createdAt: '2026-01-01T00:00:00Z', id: 1, nameL10n: { values: {} }, projectId: 1, sheetCount: 1, sheetStates: { GEN: 'Draft' }, hasLateEdits: false });
      }

      return jsonResponse(null);
    }),
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

function renderPage(): void {
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
}

describe('DocumentPage: «З дозволу» і «Оновити з дозволу» (PM-11)', () => {
  it(
    'з Template.View показує позначку й кнопку для таблиці з прив’язкою; кнопка шле POST за кодом і періодом',
    async () => {
      mockFetch(['Document.View', 'Template.View']);
      renderPage();

      expect(await screen.findByTestId('from-permit-badge', {}, { timeout: SlowEnvTimeout })).toBeDefined();
      await userEvent.setup().click(screen.getByRole('button', { name: '⟦permits.sheetBinding.doc.refresh⟧' }));

      await waitFor(() =>
        expect(calls.some((call) => call.method === 'POST' && call.url.includes(`/api/v1/documents/1/permit-bindings/PB1_T0/refresh?periodKey=${String(Period)}`))).toBe(true),
      );
      expect(await screen.findByTestId('permit-refresh-result')).toBeDefined();
      expect(calls.some((call) => call.url.includes('/api/v1/template-versions/7/permit-bindings'))).toBe(true);
    },
    SlowEnvTimeout,
  );

  it(
    'без Template.View — смуги немає і запитів прив’язок немає',
    async () => {
      mockFetch(['Document.View']);
      renderPage();

      await screen.findByRole('tab', { name: /General/ }, { timeout: SlowEnvTimeout });
      expect(screen.queryByTestId('permit-refresh-strip')).toBeNull();
      expect(calls.some((call) => call.url.includes('permit-bindings') || call.url.includes('document-template'))).toBe(false);
    },
    SlowEnvTimeout,
  );
});
