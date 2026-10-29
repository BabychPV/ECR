import type { JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { DocumentPage } from '@/pages/DocumentPage';
import { testTheme } from '@/test/render';

/**
 * PERIODNAV: адреса з періодом поза проєктом (`ECR-PRD-0422`) - не глухий кут. Екран веде на найближчий
 * допустимий період; якщо не веде - показує дію «Go to <період>» і вибір періоду, повідомлення сервера лишається.
 */

vi.mock('@/features/methodologies/CalculationResultsPanel', () => ({
  CalculationResultsPanel: (): JSX.Element => <div data-testid="calc-stub" />,
}));

vi.mock('@/features/import/ImportPanel', () => ({
  ImportPanel: (): JSX.Element => <button type="button">import-stub</button>,
}));

const SlowEnvTimeout = 400_000;
const Valid = [202611, 202612];

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

const rejected = (): Response =>
  jsonResponse(
    { title: 'Invalid period request', status: 422, detail: 'Period 202701 does not belong', errorCode: 'ECR-PRD-0422' },
    422,
  );

const requested: string[] = [];

function mockFetch(alwaysReject: boolean, cardToo = true): void {
  requested.length = 0;
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      requested.push(url);

      if (url.includes('/api/v1/me')) {
        return jsonResponse({
          denies: [], grants: { 'Project:1': 'Manage' }, isSimulation: false, language: 'en', mustChangePassword: false,
          permissions: ['Document.View'], simulatedForUserId: null, userId: 1, userName: 'tester',
        });
      }
      if (url.includes('/api/v1/projects?')) return jsonResponse({ items: [], nextCursor: null, totalCount: 0 });
      if (url.includes('/api/v1/projects/1/periods')) {
        return jsonResponse({
          projectId: 1, periodKind: 'Monthly', currentPeriodMode: 'Auto', timeZoneId: 'UTC', policy: {},
          periods: Valid.map((periodKey, id) => ({
            id, periodKey, state: 'Open', isCurrent: false, year: 2026, sequence: id + 11,
            startsAt: '2026-01-01T00:00:00Z', endsAt: '2026-02-01T00:00:00Z', graceEndsAt: null, reopenedUntil: null,
          })),
        });
      }

      const periodKey = /periodKey=(\d+)/.exec(url)?.[1];
      // Сервер перевіряє період там, де створює екземпляри таблиць (…/tables); картка може пройти.
      const isDocument = cardToo ? /\/api\/v1\/documents\/1(\?|$|\/tables)/.test(url) : /\/api\/v1\/documents\/1\/tables\?/.test(url);
      if (isDocument && periodKey !== undefined && (alwaysReject || !Valid.includes(Number(periodKey)))) return rejected();

      if (url.includes('/validation')) return jsonResponse({ documentId: 1, periodKey: 202612, messages: [], validated: false });
      if (url.includes('/tables/status')) return jsonResponse([]);
      if (url.includes('/tables')) return jsonResponse([]);
      if (url.includes('/api/v1/documents/1')) {
        return jsonResponse({
          businessKey: 'DOC-0001', createdAt: '2026-01-01T00:00:00Z', id: 1, nameL10n: { values: {} }, projectId: 1,
          sheetCount: 0, sheetStates: {}, hasLateEdits: false,
        });
      }

      return jsonResponse(null);
    }),
  );
}

function LocationProbe(): JSX.Element {
  return <div data-testid="search">{useLocation().search}</div>;
}

function show(periodKey: number, alwaysReject: boolean, cardToo = true): void {
  mockFetch(alwaysReject, cardToo);
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter initialEntries={[`/documents/1?periodKey=${String(periodKey)}`]}>
        <QueryClientProvider client={client}>
          <LocationProbe />
          <Routes>
            <Route path="/documents/:id" element={<DocumentPage />} />
          </Routes>
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('DocumentPage: період поза проєктом', () => {
  it(
    'адреса з 202701 сама переходить на останній допустимий період',
    async () => {
      show(202701, false);

      await waitFor(() => expect(screen.getByTestId('search').textContent).toBe('?periodKey=202612'), { timeout: 20_000 });
      // Календар проєкту взято з картки БЕЗ періоду (з відхиленим періодом проєкту не знати).
      expect(requested.some((url) => /\/api\/v1\/documents\/1$/.test(url))).toBe(true);
    },
    SlowEnvTimeout,
  );

  it(
    'відхиляє лише /tables (картка пройшла) - теж веде на останній допустимий період',
    async () => {
      show(202701, false, false);

      await waitFor(() => expect(screen.getByTestId('search').textContent).toBe('?periodKey=202612'), { timeout: 20_000 });
    },
    SlowEnvTimeout,
  );
  it(
    'коли перехід не допомагає - є дія «Go to <період>», вибір періоду й повідомлення сервера',
    async () => {
      show(202701, true);

      const go = await screen.findByTestId('period-recovery-go', undefined, { timeout: 20_000 });
      expect(go.textContent).toContain('⟦document.periodGoTo');
      expect(screen.getByRole('button', { name: '⟦period.next⟧' })).toBeTruthy();
      // Сервер лишається як є - його текст видно.
      expect(screen.getAllByRole('alert').length).toBeGreaterThan(0);
    },
    SlowEnvTimeout,
  );
});
