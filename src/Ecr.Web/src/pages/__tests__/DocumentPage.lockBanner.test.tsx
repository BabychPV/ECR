import type { JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { DocumentPage } from '@/pages/DocumentPage';
import { documentLockOf } from '@/features/documents/documentLock';
import { testTheme } from '@/test/render';

/**
 * `F-18`: закритий період, архівний проєкт і поданий аркуш — екран каже ЧОМУ,
 * і не пропонує дій, які сервер однаково відхилить.
 *
 * ⛔ Живцем на стенді (документ 19, архівний проєкт, період 2025-04): банера не
 * було, «Submit», «Import from Excel» і «Recalculate» стояли активні, а сервер
 * на кожну відмовляв; банер поданого аркуша з посібника (розділ 5.3) не
 * з'являвся ніде.
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

function mockFetch(options: { projectStatus: string; periodState: string; sheetState: string }): void {
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
          permissions: ['Document.View', 'Document.Import', 'Calculation.Recalculate'],
          simulatedForUserId: null,
          userId: 1,
          userName: 'tester',
        });
      }

      if (url.includes('/api/v1/projects?')) {
        return jsonResponse({
          items: [{ id: 1, code: 'P1', status: options.projectStatus, periodCount: 12, periodKind: 'Monthly', currentPeriodId: null, timeZoneId: 'UTC', nameL10n: { values: {} } }],
          nextCursor: null,
          totalCount: 1,
        });
      }

      if (url.includes('/api/v1/projects/1/periods')) {
        return jsonResponse({
          projectId: 1,
          periodKind: 'Monthly',
          currentPeriodMode: 'Auto',
          timeZoneId: 'UTC',
          policy: {},
          periods: [{ id: 5, periodKey: Period, state: options.periodState, isCurrent: false, year: 2024, sequence: 1, startsAt: '2024-01-01T00:00:00Z', endsAt: '2024-02-01T00:00:00Z', graceEndsAt: null, reopenedUntil: null }],
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
          sheetStates: { GEN: options.sheetState },
          hasLateEdits: false,
        });
      }

      return jsonResponse(null);
    }),
  );
}

function show(options: { projectStatus: string; periodState: string; sheetState: string }): void {
  mockFetch(options);

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

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('documentLockOf — одна причина, від ширшої до вужчої', () => {
  it('архів сильніший за закритий період, закритий період — за стан аркуша', () => {
    expect(documentLockOf({ projectStatus: 'Archived', periodState: 'Closed', sheetState: 'Submitted' })).toBe('projectArchived');
    expect(documentLockOf({ projectStatus: 'Active', periodState: 'Closed', sheetState: 'Submitted' })).toBe('periodClosed');
    expect(documentLockOf({ projectStatus: 'Active', periodState: 'Scheduled', sheetState: 'Draft' })).toBe('periodNotOpen');
    expect(documentLockOf({ projectStatus: 'Active', periodState: 'Open', sheetState: 'Submitted' })).toBe('sheetSubmitted');
    expect(documentLockOf({ projectStatus: 'Active', periodState: 'Grace', sheetState: 'Approved' })).toBe('sheetApproved');
    expect(documentLockOf({ projectStatus: 'Active', periodState: 'Grace', sheetState: 'Draft' })).toBeNull();

    // ⚠ Невідоме — не причина: закрити дії через невідомість означало б
    // ховати їх на кожному відкритті, доки не приїхав запит.
    expect(documentLockOf({ projectStatus: undefined, periodState: undefined, sheetState: 'Draft' })).toBeNull();
  });
});

describe('DocumentPage: банер причини і жодної приреченої дії', () => {
  it(
    'закритий період — банер із періодом; немає «Submit», «Import», «Recalculate»',
    async () => {
      show({ projectStatus: 'Active', periodState: 'Closed', sheetState: 'Draft' });

      const banner = await screen.findByText(/document\.lock\.periodClosed/, {}, { timeout: SlowEnvTimeout });
      expect(banner.textContent).toContain(`period=${String(Period)}`);

      const row = screen.getByTestId('document-actions');
      expect(within(row).queryByRole('button', { name: '⟦document.submit⟧' })).toBeNull();
      expect(within(row).queryByRole('button', { name: /workflow\.recalculate/ })).toBeNull();
      expect(screen.queryByRole('button', { name: 'import-stub' })).toBeNull();

      // ⚠ Кнопка «Validate» лишається: перевірка нічого не змінює.
      expect(within(row).getByRole('button', { name: '⟦document.validate⟧' })).toBeTruthy();
    },
    SlowEnvTimeout,
  );

  it(
    'архівний проєкт — банер архіву',
    async () => {
      show({ projectStatus: 'Archived', periodState: 'Open', sheetState: 'Draft' });

      expect(await screen.findByText('⟦document.lock.projectArchived⟧', {}, { timeout: SlowEnvTimeout })).toBeTruthy();
      expect(screen.queryByRole('button', { name: 'import-stub' })).toBeNull();
    },
    SlowEnvTimeout,
  );

  it(
    'поданий аркуш — текст посібника (5.3), а не мовчання',
    async () => {
      show({ projectStatus: 'Active', periodState: 'Open', sheetState: 'Submitted' });

      expect(await screen.findByText('⟦grid.submittedReadOnlyHint⟧', {}, { timeout: SlowEnvTimeout })).toBeTruthy();
    },
    SlowEnvTimeout,
  );

  it(
    'відкритий період, чернетка — банера немає, дії на місці (дзеркало)',
    async () => {
      show({ projectStatus: 'Active', periodState: 'Open', sheetState: 'Draft' });

      const row = await screen.findByTestId('document-actions', {}, { timeout: SlowEnvTimeout });
      await within(row).findByRole('button', { name: '⟦document.submit⟧' }, { timeout: SlowEnvTimeout });

      expect(screen.queryByText(/document\.lock\./)).toBeNull();
      expect(screen.getByRole('button', { name: 'import-stub' })).toBeTruthy();
    },
    SlowEnvTimeout,
  );
});
