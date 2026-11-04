import type { JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { DocumentPage } from '@/pages/DocumentPage';
import { testTheme } from '@/test/render';

/**
 * Копії аркуша на сторінці документа: вкладки «<Назва> N», дії копій над смугою, «Delete copy» лише на копії.
 * Поля `sheetCopyId` / `copyOrdinal` / `displayNameL10n` / `isCopyable` — за контрактом (`schema.d.ts`); стан копії —
 * у `sheetStates` за ключем `КОД#sheetCopyId`.
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

function tableOf(tableInstanceId: number, copy: Record<string, unknown>): Record<string, unknown> {
  return {
    allowsDynamicRows: false,
    maxDynamicRows: null,
    sheetCode: 'GEN',
    sheetDefId: 1,
    sheetNameL10n: { values: { en: 'General' } },
    sheetOrdinal: 0,
    tableCode: `T${String(tableInstanceId)}`,
    tableDefId: tableInstanceId,
    tableInstanceId,
    tableNameL10n: { values: { en: 'Table' } },
    tableOrdinal: 0,
    isCopyable: true,
    ...copy,
  };
}

const requestedUrls: string[] = [];

/** TC02: `chatMessageCount` of the copy's tables in `GET …/tables` (`null` for originals). */
let copyChatMessageCount: number | null = 0;

function mockFetch(sheetStates: Record<string, string> = { GEN: 'Draft' }): void {
  requestedUrls.length = 0;
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      requestedUrls.push(url);

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

      if (url.includes('/api/v1/projects?')) return jsonResponse({ items: [], nextCursor: null, totalCount: 0 });
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
          tableOf(1, { sheetCopyId: null, copyOrdinal: 1 }),
          tableOf(2, { sheetCopyId: 77, copyOrdinal: 2, chatMessageCount: copyChatMessageCount }),
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
          sheetStates,
          hasLateEdits: false,
        });
      }

      return jsonResponse(null);
    }),
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
  copyChatMessageCount = 0;
});

function renderAt(search: string): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter initialEntries={[`/documents/1?periodKey=${String(Period)}${search}`]}>
        <QueryClientProvider client={client}>
          <Routes>
            <Route path="/documents/:id" element={<DocumentPage />} />
          </Routes>
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

describe('DocumentPage: чат копії аркуша (TC02)', () => {
  it(
    'посилання згадки ?table=КОД#sheetCopyId без ?sheet= відкриває вкладку саме цієї копії',
    async () => {
      mockFetch();
      renderAt('&table=T2%2377');

      const second = await screen.findByRole('tab', { name: /General 2/ }, { timeout: SlowEnvTimeout });

      await waitFor(() => {
        expect(second.getAttribute('aria-selected')).toBe('true');
      });
    },
    SlowEnvTimeout,
  );

});

describe('DocumentPage: копії аркуша', () => {
  it(
    'вкладки «General 1» і «General 2»; «Delete copy» з’являється лише на вкладці копії',
    async () => {
      mockFetch();
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

      const first = await screen.findByRole('tab', { name: /General 1/ }, { timeout: SlowEnvTimeout });
      const second = screen.getByRole('tab', { name: /General 2/ });
      expect(screen.getAllByRole('tab')).toHaveLength(2);
      expect(first.getAttribute('aria-selected')).toBe('true');

      // Оригінал: дії копій є, видалення — немає.
      await screen.findByTestId('sheet-copy-add', undefined, { timeout: SlowEnvTimeout });
      expect(screen.queryByTestId('sheet-copy-delete')).toBeNull();

      fireEvent.click(second);

      await waitFor(() => {
        expect(screen.getByRole('tab', { name: /General 2/ }).getAttribute('aria-selected')).toBe('true');
      });
      expect(screen.getByTestId('sheet-copy-delete')).toBeDefined();
    },
    SlowEnvTimeout,
  );

  it(
    'вкладка копії показує власний стан із ключа «GEN#77», а не стан оригіналу',
    async () => {
      mockFetch({ GEN: 'Draft', 'GEN#77': 'Submitted' });
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

      const first = await screen.findByRole('tab', { name: /General 1/ }, { timeout: SlowEnvTimeout });
      const second = screen.getByRole('tab', { name: /General 2/ });

      await waitFor(() => {
        expect(first.textContent).toContain('⟦status.sheet.Draft⟧');
        expect(second.textContent).toContain('⟦status.sheet.Submitted⟧');
      });
      expect(first.textContent).not.toContain('Submitted');
    },
    SlowEnvTimeout,
  );

  it(
    'RS: вкладка копії питає history / submission-target / approver-edit з sheetCopyId; оригінал — без нього',
    async () => {
      mockFetch({ GEN: 'Submitted', 'GEN#77': 'Submitted' });
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

      await screen.findByRole('tab', { name: /General 1/ }, { timeout: SlowEnvTimeout });
      const withPath = (path: string): string[] => requestedUrls.filter((url) => url.includes(path));

      // Оригінал: усі три запити є і жоден не несе sheetCopyId.
      await waitFor(() => {
        expect(withPath('/workflow/history?').length).toBeGreaterThan(0);
        expect(withPath('/submission-target?').length).toBeGreaterThan(0);
        expect(withPath('/approver-edit?').length).toBeGreaterThan(0);
      });
      expect(requestedUrls.filter((url) => url.includes('sheetCopyId'))).toEqual([]);

      fireEvent.click(screen.getByRole('tab', { name: /General 2/ }));

      await waitFor(() => {
        for (const path of ['/workflow/history?', '/submission-target?', '/approver-edit?']) {
          expect(withPath(path).some((url) => url.includes('sheetCopyId=77'))).toBe(true);
        }
      });
    },
    SlowEnvTimeout,
  );
});
