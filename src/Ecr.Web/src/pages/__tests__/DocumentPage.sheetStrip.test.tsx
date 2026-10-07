import type { JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { DocumentPage } from '@/pages/DocumentPage';
import { testTheme } from '@/test/render';

/**
 * b4b (макет `screen-document.js`, KIT §1.5): «← Back to Documents» у шапці; смуга аркушів —
 * ПІД сіткою (tablist після панелі), лише аркуші з відповіді API (P1: роль з одним видимим
 * аркушем бачить один); аркуш без назви — «—», а не код.
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

function mockFetch(sheetState: string, sheetName: Record<string, string> = { en: 'General' }): void {
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
            sheetNameL10n: { values: sheetName },
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

describe('DocumentPage: «← Back» і смуга аркушів за макетом (b4b)', () => {
  it(
    'посилання «← Back to Documents» веде на перелік',
    async () => {
      mockFetch('Draft');
      renderPage();

      const back = await screen.findByRole('link', { name: '← ⟦nav.backToDocuments⟧' }, { timeout: SlowEnvTimeout });
      expect(back.getAttribute('href')).toBe('/');
    },
    SlowEnvTimeout,
  );

  it(
    'смуга аркушів — після панелі сітки; роль з одним видимим аркушем бачить рівно одну вкладку',
    async () => {
      mockFetch('Draft');
      renderPage();

      await screen.findByRole('tab', { name: /General/ }, { timeout: SlowEnvTimeout });
      const strip = screen.getByRole('tablist');
      const panel = screen.getByRole('tabpanel');

      expect(strip.hasAttribute('data-doc-sheet-strip')).toBe(true);
      // DOCUMENT_POSITION_FOLLOWING: смуга стоїть ПІСЛЯ сітки, а не над нею.
      expect(panel.compareDocumentPosition(strip) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
      expect(screen.getAllByRole('tab')).toHaveLength(1);
    },
    SlowEnvTimeout,
  );

  it(
    'аркуш без назви в API — «—», а не код аркуша',
    async () => {
      mockFetch('Draft', {});
      renderPage();

      const tab = await screen.findByRole('tab', { name: /—/ }, { timeout: SlowEnvTimeout });
      expect(tab.textContent).not.toContain('GEN');
    },
    SlowEnvTimeout,
  );
});
