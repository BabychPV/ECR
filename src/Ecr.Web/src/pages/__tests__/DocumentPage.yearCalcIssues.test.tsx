import type { JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { DocumentPage } from '@/pages/DocumentPage';
import { formatPeriodKey } from '@/shared/format/periodKey';
import { testTheme } from '@/test/render';

/**
 * ✎ W15z M4 (P-1122-4: «так познач щоб користувач бачив це»): документ «рік» (`YearWithMonthColumns`, Land doc 96)
 * відкривається на ЯКОРІ року (L7a), а проблеми розрахунку (F5-R «джерело змінилось після затвердження», StepError)
 * живуть у МІСЯЦІ прогону. Доти панель читала `calculation-issues` лише якоря — і причина жовтня не показувалась ніде на
 * сторінці документа, хоча перелік документів позначав «Source changed».
 *
 * Тепер «рік» читає проблеми всіх місяців року одним запитом (`year=true`, сервер L6) і показує панель на кожен місяць
 * із проблемами (заголовок називає місяць). Копія на місяць — як було: один період, без `year=true`.
 */

vi.mock('@/features/methodologies/CalculationResultsPanel', () => ({
  CalculationResultsPanel: (): JSX.Element => <div data-testid="calc-stub" />,
}));

vi.mock('@/features/import/ImportPanel', () => ({
  ImportPanel: (): JSX.Element => <button type="button">import-stub</button>,
}));

// ✎ Огляд M4 (4): перехід до місяця річного документа — запит переходу сітки (той самий шлях, що й зауваження).
const navigation = vi.hoisted(() => ({ requests: [] as unknown[] }));
vi.mock('@/features/grid/cellNavigation', async (importOriginal) => {
  const original = await importOriginal<typeof import('@/features/grid/cellNavigation')>();
  return {
    ...original,
    requestCellNavigation: (target: unknown): void => {
      navigation.requests.push(target);
      original.requestCellNavigation(target as Parameters<typeof original.requestCellNavigation>[0]);
    },
  };
});

const SlowEnvTimeout = 400_000;
const Anchor = 209901;
const October = 209910;
// ✎ Огляд M4 (5): грудень ще не відкрито (Scheduled, D-575) — його проблем не показують.
const December = 209912;
const Calendar = [Anchor, 209909, October, December];

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function period(periodKey: number): Record<string, unknown> {
  return {
    id: periodKey, periodKey, state: periodKey === December ? 'Scheduled' : 'Open', isCurrent: false, year: Math.floor(periodKey / 100), sequence: periodKey % 100,
    startsAt: '2099-01-01T00:00:00Z', endsAt: '2099-12-31T00:00:00Z', periodEnd: '2099-12-31', graceEndsAt: null, reopenedUntil: null,
  };
}

const sourceChanged = (monthNumber: number | null) => ({
  kind: 'SourceChangedAfterApproval',
  monthNumber,
  sourcePeriodKey: October,
  sourceDocumentId: 7,
  sourceProjectCode: 'HSE',
  sourceDocumentKey: 'HSE400-2099',
  sourceApprovedAt: '2099-11-01T08:00:00Z',
  approvedAt: '2099-11-02T08:00:00Z',
  sourceChangedAt: null,
});

const stepError = (monthNumber: number | null) => ({
  tableCode: 'T0', sheetCopyId: null, rowNumber: 1, rowKey: 'R1', kind: 'StepError', methodologyCode: 'M1',
  detectedAt: '2099-11-03T08:00:00Z', monthNumber, stepCode: 'S1', errorCode: '#VALUE', affectedCount: 1, methodologyId: 1,
});

const requested: string[] = [];

interface Faults {
  /** ✎ Огляд M4 (1): календар проєкту не прийшов — якоря року не знати. */
  readonly calendarFails?: boolean;
  /** ✎ Огляд M4 (1): запит року (`year=true`) відмовив. */
  readonly yearFails?: boolean;
}

function mockFetch(layout: string, { calendarFails = false, yearFails = false }: Faults = {}): void {
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
        if (calendarFails) return jsonResponse({ title: 'boom', status: 500 }, 500);
        return jsonResponse({
          projectId: 1, periodKind: 'Monthly', currentPeriodMode: 'Auto', timeZoneId: 'UTC', policy: {},
          periods: Calendar.map(period),
        });
      }
      if (url.includes('/period-reopen')) {
        return jsonResponse({ active: null, canOpen: false, defaultOpenUntil: '2099-12-31T00:00:00Z', denial: null, periodKey: Anchor });
      }
      // ✎ M4: «рік» — проблеми ВСІХ місяців (`year=true`): жовтень має F5-R і StepError. Без прапора: якір чистий, а
      // місячна копія жовтня (control) — та сама причина одного періоду.
      if (url.includes('/calculation-issues')) {
        if (url.includes('year=true')) {
          if (yearFails) return jsonResponse({ title: 'boom', status: 500 }, 500);
          return jsonResponse({ items: [stepError(10), stepError(12)], sourceIssues: [sourceChanged(10)] });
        }
        // Відмова року: якір теж має проблему (запасна панель одного періоду має що показати).
        return url.includes(`periodKey=${String(October)}`) || yearFails
          ? jsonResponse({ items: [stepError(null)], sourceIssues: [sourceChanged(10)] })
          : jsonResponse({ items: [] });
      }
      if (url.includes('/validation')) return jsonResponse({ documentId: 1, periodKey: Anchor, messages: [], validated: true });
      if (url.includes('/tables/status')) return jsonResponse([]);
      if (url.includes('/tables')) {
        return jsonResponse([
          {
            allowsDynamicRows: false, maxDynamicRows: null, sheetCode: 'GEN', sheetDefId: 1,
            sheetNameL10n: { values: { en: 'General' } }, sheetOrdinal: 0, tableCode: 'T0', tableDefId: 1,
            tableInstanceId: 1, tableNameL10n: { values: { en: 'Table 0' } }, tableOrdinal: 0,
          },
        ]);
      }
      if (url.includes('/recall')) return jsonResponse({ canRecall: false });
      if (url.includes('/approver-edit')) return jsonResponse({ canApproverEdit: false });
      if (url.includes('/submission-target')) {
        return jsonResponse({
          targetPeriodKey: Anchor, nextPeriodKey: null,
          periods: [{ periodKey: Anchor, state: 'Open', sheetStatus: 'Draft', submittable: true }],
          monthColumns: [1, 10].map((month) => ({
            tableDefId: 1, columnCode: `M${String(month).padStart(2, '0')}`, monthNumber: month, periodKey: 209900 + month,
            sheetStatus: 'Draft', canDecide: false, canRecall: false, canReturn: false,
          })),
        });
      }
      if (url.includes('/api/v1/documents/1')) {
        return jsonResponse({
          businessKey: 'DOC-0001', createdAt: '2099-01-01T00:00:00Z', id: 1, nameL10n: { values: {} }, projectId: 1,
          sheetCount: 1, sheetStates: { GEN: 'Approved' }, hasLateEdits: false, templateLayout: layout,
        });
      }

      return jsonResponse(null);
    }),
  );
}

function show(initial: string, layout: string, faults: Faults = {}): void {
  mockFetch(layout, faults);
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

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('DocumentPage: проблеми розрахунку документа «рік» — по місяцях (W15z M4)', () => {
  it(
    'рік на якорі: панель жовтня з причиною F5-R (джерело й місяць) і StepError жовтня; запит year=true',
    async () => {
      show(`/documents/1?periodKey=${String(Anchor)}`, 'YearWithMonthColumns');

      const panel = await waitFor(
        () => {
          const found = document.querySelector<HTMLElement>(`[data-testid="document-calc-issues"][data-period="${String(October)}"]`);
          if (found === null) throw new Error('панелі жовтня немає');
          return found;
        },
        { timeout: SlowEnvTimeout },
      );

      expect(within(panel).getByTestId('document-calc-issues-title').textContent).toContain(formatPeriodKey(October));
      const kinds = within(panel).getAllByTestId('document-calc-issue').map((row) => row.getAttribute('data-kind'));
      expect(kinds).toEqual(['sourceChanged', 'stepError']);
      const cause = within(panel).getAllByTestId('document-calc-issue')[0]!;
      expect(within(cause).getByTestId('document-calc-issue-detail').textContent).toBe('HSE · HSE400-2099');
      expect(within(cause).getByTestId('document-calc-issue-source-hint')).toBeTruthy();
      expect(within(cause).getByTestId('document-calc-issue-open-source')).toBeTruthy();
      expect(requested.some((url) => url.includes('/calculation-issues') && url.includes('year=true'))).toBe(true);
      // ✎ Огляд M4 (3): лише `Document.View` (як у сервера для calculation-issues) — рік читається; час прогону
      // (`calculation-run`, право `Calculation.View`) без права не запитується.
      expect(requested.some((url) => url.includes('/calculation-run'))).toBe(false);
      // Якір (січень) чистий — його панелі немає; панелей рівно одна (жовтень).
      expect(screen.getAllByTestId('document-calc-issues')).toHaveLength(1);
    },
    SlowEnvTimeout,
  );

  it(
    'рік: місяць Scheduled (грудень, не відкрито — D-575) панелі проблем не має, жовтень — має',
    async () => {
      show(`/documents/1?periodKey=${String(Anchor)}`, 'YearWithMonthColumns');

      await waitFor(
        () => {
          if (document.querySelector(`[data-testid="document-calc-issues"][data-period="${String(October)}"]`) === null) {
            throw new Error('панелі жовтня немає');
          }
        },
        { timeout: 60_000 },
      );
      expect(document.querySelector(`[data-testid="document-calc-issues"][data-period="${String(December)}"]`)).toBeNull();
    },
    SlowEnvTimeout,
  );

  it(
    'рік: посилання на місяць (?periodKey=жовтень) — якір для даних, перехід сітки до колонки жовтня',
    async () => {
      navigation.requests.length = 0;
      show(`/documents/1?periodKey=${String(October)}`, 'YearWithMonthColumns');

      await waitFor(
        () => expect(navigation.requests).toContainEqual(expect.objectContaining({ tableDefId: 1, columnCode: 'M10' })),
        { timeout: 60_000 },
      );
      expect(requested).toContain(`/api/v1/documents/1/tables?periodKey=${String(Anchor)}`);
    },
    SlowEnvTimeout,
  );

  it(
    'рік без місяця в адресі (якір): переходу сітки до колонки немає',
    async () => {
      navigation.requests.length = 0;
      show(`/documents/1?periodKey=${String(Anchor)}`, 'YearWithMonthColumns');

      await waitFor(
        () => {
          if (document.querySelector(`[data-testid="document-calc-issues"][data-period="${String(October)}"]`) === null) {
            throw new Error('сторінка ще не готова');
          }
        },
        { timeout: 60_000 },
      );
      expect(navigation.requests).toEqual([]);
    },
    SlowEnvTimeout,
  );

  it(
    'рік без календаря (якоря не знати): панель поточного періоду адреси, як до M4',
    async () => {
      show(`/documents/1?periodKey=${String(October)}`, 'YearWithMonthColumns', { calendarFails: true });

      const panel = await waitFor(
        () => {
          const found = document.querySelector<HTMLElement>(`[data-testid="document-calc-issues"][data-period="${String(October)}"]`);
          if (found === null) throw new Error('запасної панелі немає');
          return found;
        },
        { timeout: 60_000 },
      );
      expect(within(panel).getAllByTestId('document-calc-issue').map((row) => row.getAttribute('data-kind'))).toEqual(['sourceChanged', 'stepError']);
    },
    SlowEnvTimeout,
  );

  it(
    'рік, запит року відмовив: панель якоря (один період), а не порожнеча',
    async () => {
      show(`/documents/1?periodKey=${String(Anchor)}`, 'YearWithMonthColumns', { yearFails: true });

      await waitFor(() => expect(requested.some((url) => url.includes('year=true'))).toBe(true), { timeout: 60_000 });
      const panel = await waitFor(
        () => {
          const found = document.querySelector<HTMLElement>(`[data-testid="document-calc-issues"][data-period="${String(Anchor)}"]`);
          if (found === null) throw new Error('запасної панелі немає');
          return found;
        },
        { timeout: 60_000 },
      );
      expect(within(panel).getAllByTestId('document-calc-issue').length).toBeGreaterThan(0);
    },
    SlowEnvTimeout,
  );

  it(
    'копія на місяць (control): одна панель свого періоду з тією самою причиною, без year=true',
    async () => {
      show(`/documents/1?periodKey=${String(October)}`, 'PerMonthCopies');

      const panel = await screen.findByTestId('document-calc-issues', {}, { timeout: SlowEnvTimeout });
      expect(within(panel).getByTestId('document-calc-issues-title').textContent).toContain(formatPeriodKey(October));
      const kinds = within(panel).getAllByTestId('document-calc-issue').map((row) => row.getAttribute('data-kind'));
      expect(kinds).toEqual(['sourceChanged', 'stepError']);
      expect(screen.getAllByTestId('document-calc-issues')).toHaveLength(1);
      expect(requested.some((url) => url.includes('/calculation-issues') && url.includes('year=true'))).toBe(false);
    },
    SlowEnvTimeout,
  );
});
