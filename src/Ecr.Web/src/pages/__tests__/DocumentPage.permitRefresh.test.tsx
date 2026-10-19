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
 * «З дозволу» і «Оновити з дозволу» в сторінці документа (Permits, PM-11/PM-11r2): смугу вирішує
 * `permitBinding` у відповіді `…/tables`, яку сторінка і так читає. Жодних запитів до `document-template` і
 * `permit-bindings` (перелік), права `Template.View` не потрібно.
 *
 * Мутаційний доказ: `tables={active.tables}` → `tables={[]}` валить «показує смугу»; повернути в смугу запит переліку
 * прив'язок валить «жодних запитів».
 * ⚠ Чого тест НЕ розрізняє: умова монтування в `DocumentPage` (`active.tables.some(table => table.permitBinding != null)`)
 * лише вирішує, чи вантажити lazy-чанк смуги, а сама смуга без прив'язок Rows повертає `null`. Заміна умови на `true`
 * чи `active.tables.length >= 0` у DOM нічого не змінює, тож тести цього не ловлять (мутація виживає); відсутність
 * чанка без прив'язок не перевіряється.
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

function mockFetch(permissions: string[], permitBinding: { code: string; mode: string; permitTypeCode: string } | null): void {
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
      // Якби смуга знову ходила по версію шаблону чи перелік прив'язок, ці відповіді були б відмовою.
      if (url.includes('/document-template') || url.includes('/permit-bindings')) return jsonResponse({ title: 'Forbidden' }, 403);
      if (url.includes('/api/v1/projects?')) return jsonResponse({ items: [], nextCursor: null, totalCount: 0 });
      if (url.includes('/api/v1/projects/1/periods')) {
        return jsonResponse({ projectId: 1, periodKind: 'Monthly', currentPeriodMode: 'Auto', timeZoneId: 'UTC', policy: {}, periods: [] });
      }
      if (url.includes('/validation')) return jsonResponse({ documentId: 1, periodKey: Period, messages: [], validated: false });
      if (url.includes('/tables/status')) return jsonResponse([]);
      if (url.includes('/tables')) {
        return jsonResponse([
          { allowsDynamicRows: true, maxDynamicRows: 100, sheetCode: 'GEN', sheetDefId: 1, sheetNameL10n: { values: { en: 'General' } }, sheetOrdinal: 0, tableCode: 'T0', tableDefId: 1, tableInstanceId: 1, tableNameL10n: { values: { en: 'Table 0' } }, tableOrdinal: 0, permitBinding },
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

const permitRequests = (): string[] =>
  calls.filter((call) => call.url.includes('permit-bindings') || call.url.includes('document-template')).map((call) => `${call.method} ${call.url}`);

describe('DocumentPage: «З дозволу» і «Оновити з дозволу» (PM-11r2)', () => {
  it(
    'permitBinding Rows у відповіді tables: смуга з кнопкою; кнопка шле POST за кодом і періодом; жодних інших запитів прив’язок',
    async () => {
      mockFetch(['Document.View'], { code: 'PB1_T0', mode: 'Rows', permitTypeCode: 'AirPermit' });
      renderPage();

      expect(await screen.findByTestId('from-permit-badge', {}, { timeout: SlowEnvTimeout })).toBeDefined();
      await userEvent.setup().click(screen.getByRole('button', { name: '⟦permits.sheetBinding.doc.refresh⟧' }));

      expect(await screen.findByTestId('permit-refresh-result')).toBeDefined();
      expect(permitRequests()).toEqual([`POST /api/v1/documents/1/permit-bindings/PB1_T0/refresh?periodKey=${String(Period)}`]);
    },
    SlowEnvTimeout,
  );

  it(
    'Template.View є, прив’язок нема (permitBinding null): смуги немає і жодного запиту до permit-bindings/document-template',
    async () => {
      mockFetch(['Document.View', 'Template.View'], null);
      renderPage();

      await screen.findByRole('tab', { name: /General/ }, { timeout: SlowEnvTimeout });
      expect(screen.queryByTestId('permit-refresh-strip')).toBeNull();
      await waitFor(() => expect(calls.some((call) => call.url.includes('/tables'))).toBe(true));
      expect(permitRequests()).toEqual([]);
    },
    SlowEnvTimeout,
  );

  it(
    'режим Formulas: смуги немає',
    async () => {
      mockFetch(['Document.View', 'Template.View'], { code: 'PB1_T0', mode: 'Formulas', permitTypeCode: 'AirPermit' });
      renderPage();

      await screen.findByRole('tab', { name: /General/ }, { timeout: SlowEnvTimeout });
      expect(screen.queryByTestId('permit-refresh-strip')).toBeNull();
      expect(permitRequests()).toEqual([]);
    },
    SlowEnvTimeout,
  );
});
