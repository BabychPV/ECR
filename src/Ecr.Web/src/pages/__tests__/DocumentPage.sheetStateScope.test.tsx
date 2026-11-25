import type { JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { DocumentPage } from '@/pages/DocumentPage';
import { testTheme } from '@/test/render';

/**
 * ⛔ AC-2 (b14 documents-board-yearly, людина 25.11: «Sheet: Draft» без місяця — «це не зрозумів чому»): чип стану
 * аркуша каже, ЧИЙ це стан. Документ «рік» — стан аркуша за рік (зведення місяців тим самим правилом, що й стан
 * документа за рік у переліку) і «Year 2026»; копія на місяць — назва місяця, а відкритий лише для документа місяць —
 * ще й «Reopened for this document».
 *
 * Без фіксу червоний: поруч із чипом немає підпису періоду, а в документі «рік» чип — «Draft» якоря (січень), хоч
 * січень–серпень подано.
 */

vi.mock('@/features/methodologies/CalculationResultsPanel', () => ({
  CalculationResultsPanel: (): JSX.Element => <div data-testid="calc-stub" />,
}));

vi.mock('@/features/import/ImportPanel', () => ({
  ImportPanel: (): JSX.Element => <button type="button">import-stub</button>,
}));

const SlowEnvTimeout = 400_000;
// Поява підпису після відповіді — секунди; довше чекати означало б лише довше падати без фіксу.
const FindTimeout = 30_000;
const Year = 2026;
const Anchor = Year * 100 + 1;
const September = Year * 100 + 9;

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function period(periodKey: number, state: string): Record<string, unknown> {
  const sequence = periodKey % 100;
  return {
    id: sequence,
    periodKey,
    state,
    isCurrent: periodKey === September,
    year: Year,
    sequence,
    startsAt: `${String(Year)}-01-01T00:00:00Z`,
    endsAt: `${String(Year)}-12-31T00:00:00Z`,
    periodEnd: `${String(Year)}-12-31`,
    graceEndsAt: null,
    reopenedUntil: null,
  };
}

const months = Array.from({ length: 12 }, (_, index) => Anchor + index);

interface Setup {
  readonly templateLayout: string | null;
  /** Стан місяця в календарі проєкту. */
  readonly periodState: (periodKey: number) => string;
  /** Стан аркуша за місяць (`submission-target.monthColumns`); `null` — колонок немає. */
  readonly monthStatus: ((periodKey: number) => string) | null;
  readonly sheetState: string;
  /** Діючий виняток «місяць відкрито для документа». */
  readonly reopened?: boolean;
}

function mockFetch(setup: Setup): void {
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
      if (url.includes('/api/v1/projects?')) return jsonResponse({ items: [], nextCursor: null, totalCount: 0 });
      if (url.includes('/api/v1/projects/1/periods')) {
        return jsonResponse({
          projectId: 1,
          periodKind: 'Monthly',
          currentPeriodMode: 'Auto',
          timeZoneId: 'UTC',
          policy: {},
          periods: months.map((key) => period(key, setup.periodState(key))),
        });
      }
      if (url.includes('/period-reopen')) {
        return jsonResponse({
          active:
            setup.reopened === true
              ? { openUntil: '2999-01-01T00:00:00Z', reason: 'fix', openedByName: 'Coordinator', openedByUserId: 2 }
              : null,
          canOpen: false,
          defaultOpenUntil: '2999-01-01T00:00:00Z',
          denial: null,
          periodKey: September,
        });
      }
      if (url.includes('/validation')) return jsonResponse({ documentId: 1, periodKey: Anchor, messages: [], validated: false });
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
      if (url.includes('/recall')) return jsonResponse({ canRecall: false });
      if (url.includes('/approver-edit')) return jsonResponse({ canApproverEdit: false });
      if (url.includes('/submission-target')) {
        const status = setup.monthStatus;
        return jsonResponse({
          targetPeriodKey: null,
          nextPeriodKey: null,
          periods: [],
          monthColumns:
            status === null
              ? []
              : months.map((key) => ({
                  canDecide: false,
                  canRecall: false,
                  canReturn: false,
                  columnCode: `M${String(key % 100)}`,
                  monthNumber: key % 100,
                  periodKey: key,
                  sheetStatus: status(key),
                  tableDefId: 1,
                })),
        });
      }
      if (url.includes('/api/v1/documents/1')) {
        return jsonResponse({
          businessKey: 'DOC-0001',
          createdAt: '2026-01-01T00:00:00Z',
          id: 1,
          nameL10n: { values: {} },
          projectId: 1,
          periodKind: 'Monthly',
          sheetCount: 1,
          sheetStates: { GEN: setup.sheetState },
          hasLateEdits: false,
          templateLayout: setup.templateLayout,
        });
      }

      return jsonResponse(null);
    }),
  );
}

function show(initial: string): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter initialEntries={[initial]}>
        <QueryClientProvider client={client}>
          <Routes>
            <Route path="/documents/:id" element={<DocumentPage />} />
          </Routes>
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

const scope = async (): Promise<HTMLElement> => screen.findByTestId('document-sheet-state-scope', {}, { timeout: FindTimeout });

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('DocumentPage: чий стан у чипі «Sheet:» (AC-2, b14)', () => {
  it(
    'AC-2a: документ «рік»: січ–сер подано, вересень перевідкрито й у чернетці — «Partly submitted · Year 2026», а не голе «Draft» якоря',
    async () => {
      mockFetch({
        templateLayout: 'YearWithMonthColumns',
        periodState: (key) => (key < September ? 'Closed' : key === September ? 'Open' : 'Scheduled'),
        // Жовтень–грудень ще не відкрито: їхній `Draft` у рік не входить.
        monthStatus: (key) => (key < September ? 'Submitted' : 'Draft'),
        sheetState: 'Draft',
      });
      show(`/documents/1?periodKey=${String(Anchor)}`);

      await waitFor(
        async () => {
          const progress = screen.getByTestId('document-progress');
          expect(progress.textContent).toContain('status.sheet.PartlySubmitted');
          expect(progress.textContent).not.toContain('status.sheet.Draft');
        },
        { timeout: FindTimeout },
      );
      const text = (await scope()).textContent ?? '';
      expect(text).toMatch(/calcResults\.yearChip/);
      expect(text).toContain(String(Year));
    },
    SlowEnvTimeout,
  );

  it(
    'AC-2b: документ «рік» без місячних колонок — стан якоря з назвою його місяця',
    async () => {
      mockFetch({
        templateLayout: 'YearWithMonthColumns',
        periodState: () => 'Open',
        monthStatus: null,
        sheetState: 'Draft',
      });
      show(`/documents/1?periodKey=${String(Anchor)}`);

      const text = (await scope()).textContent ?? '';
      expect(text).toMatch(/January 2026|periods\.monthOf/);
      expect(screen.getByTestId('document-progress').textContent).toContain('status.sheet.Draft');
    },
    SlowEnvTimeout,
  );

  it(
    'AC-2c: копія на місяць: місяць, відкритий лише для документа, — «September 2026 · Reopened for this document»',
    async () => {
      mockFetch({
        templateLayout: null,
        periodState: (key) => (key <= September ? 'Closed' : 'Scheduled'),
        monthStatus: null,
        sheetState: 'Draft',
        reopened: true,
      });
      show(`/documents/1?periodKey=${String(September)}`);

      await waitFor(
        async () => {
          const text = (await scope()).textContent ?? '';
          expect(text).toMatch(/September 2026|periods\.monthOf/);
          expect(text).toContain('documents.strip.reopened');
        },
        { timeout: FindTimeout },
      );
    },
    SlowEnvTimeout,
  );
});
