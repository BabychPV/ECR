import type { JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { DocumentPage } from '@/pages/DocumentPage';
import { isYearLayout, yearAnchorCalendar } from '@/features/documents/yearLayout';
import { testTheme } from '@/test/render';

/**
 * ✎ D-474, п. 5: «вибір місяця в документі показується лише там, де документ має кілька копій».
 *
 * ⛔ Мутаційний доказ: прибрати умову `yearLayout` у шапці — тест «рік» червоніє (перемикач є); прибрати
 * `yearAnchorCalendar` — документ без `?periodKey` відкривається на поточному місяці (201503), а не на якорі року
 * (201501). Тест «копія на місяць» тримає незмінність старої поведінки (поточний місяць + перемикач).
 */

vi.mock('@/features/methodologies/CalculationResultsPanel', () => ({
  CalculationResultsPanel: (): JSX.Element => <div data-testid="calc-stub" />,
}));

vi.mock('@/features/import/ImportPanel', () => ({
  ImportPanel: (): JSX.Element => <button type="button">import-stub</button>,
}));

const SlowEnvTimeout = 400_000;
const Anchor = 201501;
const ProjectCurrent = 201503;

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function period(periodKey: number, isCurrent: boolean): Record<string, unknown> {
  const sequence = periodKey % 100;
  return {
    id: sequence,
    periodKey,
    state: 'Open',
    isCurrent,
    year: 2015,
    sequence,
    startsAt: '2015-01-01T00:00:00Z',
    endsAt: '2015-12-31T00:00:00Z',
    periodEnd: '2015-12-31',
    graceEndsAt: null,
    reopenedUntil: null,
  };
}

function mockFetch(templateLayout: string | null): ReturnType<typeof vi.fn> {
  const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
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
        periods: [period(201412, false), period(Anchor, false), period(201502, false), period(ProjectCurrent, true)],
      });
    }

    if (url.includes('/validation')) return jsonResponse({ documentId: 1, periodKey: ProjectCurrent, messages: [], validated: false });
    if (url.includes('/tables/status')) return jsonResponse([]);
    // ⚠ Один аркуш: документ без аркушів малює порожній стан замість шапки — перемикача там немає в обох розкладках.
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
    // ⚠ Подання за місяцем — власний вибір цілі (`submission-target`), не перемикач шапки: лишається в обох розкладках.
    if (url.includes('/submission-target')) {
      return jsonResponse({
        targetPeriodKey: Anchor,
        nextPeriodKey: null,
        periods: [{ periodKey: Anchor, state: 'Open', sheetStatus: 'Draft', submittable: true }],
        monthColumns: [],
      });
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
        hasLateEdits: false,
        templateLayout,
      });
    }

    return jsonResponse(null);
  });

  vi.stubGlobal('fetch', fetchMock);

  return fetchMock;
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

const calledUrls = (fetchMock: ReturnType<typeof vi.fn>): string[] =>
  fetchMock.mock.calls.map((call: unknown[]) => String(call[0]));

const periodPicker = (): Element | null => document.querySelector('.ecr-period-anchor');

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('yearLayout', () => {
  it('рік — лише YearWithMonthColumns; null і PerMonthCopies — ні', () => {
    expect(isYearLayout('YearWithMonthColumns')).toBe(true);
    expect(isYearLayout('PerMonthCopies')).toBe(false);
    expect(isYearLayout(null)).toBe(false);
    expect(isYearLayout(undefined)).toBe(false);
  });

  it('позначка «поточний» переходить на якір року поточного періоду (найменший ключ того ж року)', () => {
    expect(
      yearAnchorCalendar([
        { periodKey: 201412 },
        { periodKey: 201502 },
        { periodKey: 201501 },
        { periodKey: 201503, isCurrent: true },
      ]),
    ).toEqual([{ periodKey: 201501, isCurrent: true }]);
  });

  it('без позначки поточного — календар як є', () => {
    const periods = [{ periodKey: 201501 }, { periodKey: 201502 }];
    expect(yearAnchorCalendar(periods)).toBe(periods);
    expect(yearAnchorCalendar(undefined)).toBeUndefined();
  });
});

describe('DocumentPage: розкладка версії і вибір місяця (D-474, п. 5)', () => {
  it(
    'рік, місяці в колонках: перемикача місяця немає, без ?periodKey відкривається якір року',
    async () => {
      const fetchMock = mockFetch('YearWithMonthColumns');
      show('/documents/1');

      await waitFor(
        () => {
          expect(calledUrls(fetchMock)).toContain(`/api/v1/documents/1/tables?periodKey=${String(Anchor)}`);
        },
        { timeout: SlowEnvTimeout },
      );
      // Шапка намальована (рядок дій з поданням є) — і перемикача в ній немає; Submit лишився.
      const row = await screen.findByTestId('document-actions', {}, { timeout: SlowEnvTimeout });
      await within(row).findByRole('button', { name: /workflow\.submitPeriod/ }, { timeout: SlowEnvTimeout });
      expect(calledUrls(fetchMock)).not.toContain(`/api/v1/documents/1/tables?periodKey=${String(ProjectCurrent)}`);
      expect(periodPicker()).toBeNull();
    },
    SlowEnvTimeout,
  );

  it(
    'копія на місяць (null): як до D-474 — поточний період проєкту і перемикач місяця',
    async () => {
      const fetchMock = mockFetch(null);
      show('/documents/1');

      await waitFor(
        () => {
          expect(calledUrls(fetchMock)).toContain(`/api/v1/documents/1/tables?periodKey=${String(ProjectCurrent)}`);
        },
        { timeout: SlowEnvTimeout },
      );
      await waitFor(() => expect(periodPicker()).not.toBeNull(), { timeout: SlowEnvTimeout });
    },
    SlowEnvTimeout,
  );
});
