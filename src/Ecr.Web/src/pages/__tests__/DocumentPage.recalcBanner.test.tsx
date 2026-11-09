import type { JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { DocumentPage } from '@/pages/DocumentPage';
import { testTheme } from '@/test/render';

/**
 * Бюджет D-132: місце банера перерахунку проєкту на сторінці документа винесено за `import()` (DocumentPage був
 * 250.5 КБ gzip після зведення з #782). Лінивий чанк має так само доїхати й показати банер, коли перерахунок
 * проєкту документа щойно завершився. Мутаційний доказ: прибрати `<ProjectRecalcBannerSlot>` у `DocumentPage.tsx` —
 * тест червоний.
 */

vi.mock('@/features/methodologies/CalculationResultsPanel', () => ({
  CalculationResultsPanel: (): JSX.Element => <div data-testid="calc-stub" />,
}));

vi.mock('@/features/import/ImportPanel', () => ({
  ImportPanel: (): JSX.Element => <button type="button">import-stub</button>,
}));

const Slow = 400_000;
const Wait = 150_000;
const Period = 202610;

const finishedRecalc = {
  jobId: 'RC-1',
  jobCode: 'Ecr.Application.Ports.IRecalculationJob',
  state: 'Succeeded',
  effectiveState: 'Succeeded',
  percent: 100,
  updatedAt: new Date().toISOString(),
  startedAt: new Date().toISOString(),
  documentId: null,
  projectId: 1,
  projectCode: 'P1',
  fanOut: { total: 3, queued: 0, running: 0, succeeded: 3, failed: 0, skippedSheets: 0 },
};

function json(body: unknown): Response {
  return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
}

let requested: string[] = [];

function serve(): void {
  requested = [];
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      requested.push(url);

      if (url.includes('/api/v1/me')) {
        return json({
          denies: [],
          grants: { 'Project:1': 'Manage' },
          isSimulation: false,
          language: 'en',
          mustChangePassword: false,
          permissions: ['Document.View', 'Calculation.Recalculate'],
          simulatedForUserId: null,
          userId: 1,
          userName: 'tester',
        });
      }
      if (url.includes('/api/v1/jobs')) return json([finishedRecalc]);
      if (url.includes('/api/v1/projects?')) {
        return json({
          items: [{ id: 1, code: 'P1', status: 'Active', periodCount: 12, periodKind: 'Monthly', currentPeriodId: null, timeZoneId: 'UTC', nameL10n: { values: {} } }],
          nextCursor: null,
          totalCount: 1,
        });
      }
      if (url.includes('/api/v1/projects/1/periods')) {
        return json({
          projectId: 1,
          periodKind: 'Monthly',
          currentPeriodMode: 'Auto',
          timeZoneId: 'UTC',
          policy: {},
          periods: [{ id: 5, periodKey: Period, state: 'Open', isCurrent: true, year: 2026, sequence: 10, startsAt: '2026-10-01T00:00:00Z', endsAt: '2026-11-01T00:00:00Z', graceEndsAt: null, reopenedUntil: null }],
        });
      }
      if (url.includes('/validation')) return json({ documentId: 1, periodKey: Period, messages: [], validated: false });
      if (url.includes('/tables/status')) return json([]);
      if (url.includes('/tables')) {
        return json([
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
      if (url.includes('/submission-target')) {
        return json({ targetPeriodKey: Period, nextPeriodKey: null, periods: [], monthColumns: [] });
      }
      if (url.includes('/api/v1/documents/1')) {
        return json({
          businessKey: 'DOC-0001',
          createdAt: '2026-01-01T00:00:00Z',
          id: 1,
          nameL10n: { values: {} },
          projectId: 1,
          sheetCount: 1,
          sheetStates: { GEN: 'Draft' },
          hasLateEdits: false,
        });
      }

      return json(null);
    }),
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
  globalThis.localStorage?.clear();
});

describe('DocumentPage: банер перерахунку проєкту — лінивим чанком (D-132)', () => {
  it(
    'щойно завершений перерахунок проєкту документа — банер на місці',
    async () => {
      serve();
      render(
        <MantineProvider theme={testTheme}>
          <MemoryRouter initialEntries={[`/documents/1?periodKey=${String(Period)}`]}>
            <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
              <Routes>
                <Route path="/documents/:id" element={<DocumentPage />} />
              </Routes>
            </QueryClientProvider>
          </MemoryRouter>
        </MantineProvider>,
      );

      // Сторінка відомо знає свій проєкт (документ прочитано) — і банер читає перерахунки саме його.
      await waitFor(() => expect(requested.some((url) => url.includes('/api/v1/jobs?') && url.includes('projectId=1'))).toBe(true), {
        timeout: Wait,
      });
      await waitFor(() => expect(document.querySelector('[data-recalc-banner="RC-1"]')).not.toBeNull(), { timeout: Wait });
      expect(document.querySelector('[data-recalc-banner="RC-1"]')?.getAttribute('data-recalc-phase')).toBe('finished');
    },
    Slow,
  );
});
