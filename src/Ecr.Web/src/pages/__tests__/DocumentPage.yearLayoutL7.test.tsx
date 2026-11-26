import type { JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { DocumentPage } from '@/pages/DocumentPage';
import { yearAnchorOf, yearAnchors } from '@/features/documents/yearLayout';
import { formatPeriodKey } from '@/shared/format/periodKey';
import { testTheme } from '@/test/render';

/**
 * ✎ LAND-YEAR L7a/L7b — сторінка документа «рік» (`YearWithMonthColumns`): без перемикача місяця в будь-якому вигляді.
 *
 * Кожна гілка має пару «рік» / «копія на місяць» (регресія: місячний документ — як до L7).
 * ⛔ Мутаційний доказ (READY): `yearAnchor` → `null` — червоний «?periodKey місяця → якір»; `yearDocument` у запиті
 * перевірки → `false` — червоні «Issues за рік»; `!yearLayout` у панелі результатів прибрано — червоний «панель
 * прихована»; `yearLayout` банера → `false` — червоний «банер Year»; `pickablePeriods` = `projectPeriods` —
 * червоний «запасний перехід на якір».
 */

vi.mock('@/features/methodologies/CalculationResultsPanel', () => ({
  CalculationResultsPanel: (): JSX.Element => <div data-testid="calc-stub" />,
}));

vi.mock('@/features/import/ImportPanel', () => ({
  ImportPanel: (): JSX.Element => <button type="button">import-stub</button>,
}));

const SlowEnvTimeout = 400_000;
const Anchor = 209901;
const Calendar = [209812, Anchor, 209909, 209910, 210001, 210002];

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

const rejected = (): Response =>
  jsonResponse({ title: 'Invalid period request', status: 422, detail: 'x', errorCode: 'ECR-PRD-0422' }, 422);

function period(periodKey: number, state: string): Record<string, unknown> {
  const sequence = periodKey % 100;
  return {
    id: periodKey,
    periodKey,
    state,
    isCurrent: false,
    year: Math.floor(periodKey / 100),
    sequence,
    startsAt: '2099-01-01T00:00:00Z',
    endsAt: '2099-12-31T00:00:00Z',
    periodEnd: '2099-12-31',
    graceEndsAt: null,
    reopenedUntil: null,
  };
}

const finding = (periodKey: number, rowKey: string): Record<string, unknown> => ({
  severity: 'Error',
  ruleCode: 'REQ',
  message: `required ${rowKey}`,
  tableDefId: 1,
  rowKey,
  columnCode: 'M10',
  blocksSave: false,
  displayCode: null,
  periodKey,
});

interface Options {
  readonly layout: string | null;
  readonly state?: string;
  readonly permissions?: readonly string[];
  readonly rejectAll?: boolean;
  /** ✎ L7c: діючі винятки року в картці (`reopenedPeriods`). */
  readonly reopened?: readonly { readonly periodKey: number; readonly openUntil: string }[];
  /** ✎ L7c: GET `/period-reopen` якоря повертає діючий виняток. */
  readonly anchorActive?: boolean;
}

const requested: string[] = [];

function mockFetch({
  layout, state = 'Open', permissions = ['Document.View'], rejectAll = false, reopened, anchorActive = false,
}: Options): void {
  requested.length = 0;
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      requested.push(url);

      if (url.includes('/api/v1/me')) {
        return jsonResponse({
          denies: [], grants: { 'Project:1': 'Manage' }, isSimulation: false, language: 'en', mustChangePassword: false,
          permissions, simulatedForUserId: null, userId: 1, userName: 'tester',
        });
      }
      if (url.includes('/api/v1/projects?')) return jsonResponse({ items: [], nextCursor: null, totalCount: 0 });
      if (url.includes('/api/v1/projects/1/periods')) {
        return jsonResponse({
          projectId: 1, periodKind: 'Monthly', currentPeriodMode: 'Auto', timeZoneId: 'UTC', policy: {},
          periods: Calendar.map((key) => period(key, state)),
        });
      }
      if (url.includes('/period-reopen')) {
        const active = anchorActive
          ? { openUntil: '2099-12-20T18:00:00Z', reason: 'L7c anchor', openedByUserId: 1, openedByName: 'tester' }
          : null;
        return jsonResponse({ active, canOpen: false, defaultOpenUntil: '2099-12-31T00:00:00Z', denial: null, periodKey: Anchor });
      }
      if (rejectAll && /\/api\/v1\/documents\/1(\?|\/tables\?)/.test(url)) return rejected();

      // ✎ Прогін (POST validate) пише результат ОДНОГО періоду — якоря, тут чистий.
      if (url.includes('/validate')) return jsonResponse({ documentId: 1, periodKey: Anchor, messages: [], validated: true });
      // ✎ L6: `year=true` — останні результати всіх місяців року (жовтень і вересень); без прапора — лише якір (чисто).
      if (url.includes('/validation')) {
        return url.includes('year=true')
          ? jsonResponse({ documentId: 1, periodKey: Anchor, messages: [finding(209910, 'R1'), finding(209909, 'R2')], validated: true })
          : jsonResponse({ documentId: 1, periodKey: Anchor, messages: [], validated: true });
      }
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
          periods: [{ periodKey: Anchor, state: 'Open', sheetStatus: 'Draft', submittable: true }], monthColumns: [],
        });
      }
      if (url.includes('/api/v1/documents/1')) {
        return jsonResponse({
          businessKey: 'DOC-0001', createdAt: '2099-01-01T00:00:00Z', id: 1, nameL10n: { values: {} }, projectId: 1,
          sheetCount: 1, sheetStates: { GEN: 'Draft' }, hasLateEdits: false, templateLayout: layout,
          ...(reopened === undefined ? {} : { reopenedPeriods: reopened }),
        });
      }

      return jsonResponse(null);
    }),
  );
}

function LocationProbe(): JSX.Element {
  return <div data-testid="search">{useLocation().search}</div>;
}

function show(initial: string, options: Options): void {
  mockFetch(options);
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter initialEntries={[initial]}>
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

const search = (): string | null => screen.getByTestId('search').textContent;
const actionsRow = (): Promise<HTMLElement> => screen.findByTestId('document-actions', {}, { timeout: SlowEnvTimeout });
const issuesCount = async (): Promise<string | null> => {
  const button = await waitFor(
    () => {
      const found = document.querySelector('[data-inspector-issues]');
      if (found === null || found.getAttribute('data-inspector-issues') === 'none') throw new Error('not yet');
      return found;
    },
    { timeout: SlowEnvTimeout },
  );
  return button.getAttribute('data-inspector-issues');
};

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('yearAnchorOf / yearAnchors', () => {
  it('якір — найменший місяць того ж року в календарі; невідомий рік або календар — null', () => {
    expect(yearAnchorOf(Calendar, 209910)).toBe(Anchor);
    expect(yearAnchorOf(Calendar, 210002)).toBe(210001);
    expect(yearAnchorOf(Calendar, 209812)).toBe(209812);
    expect(yearAnchorOf(Calendar, 205005)).toBeNull();
    expect(yearAnchorOf(undefined, 209910)).toBeNull();
  });

  it('якорі всіх років календаря за зростанням (без місяців)', () => {
    expect(yearAnchors([210002, 209910, Anchor, 209812, 210001])).toEqual([209812, Anchor, 210001]);
    expect(yearAnchors(undefined)).toBeUndefined();
  });
});

describe('DocumentPage «рік» L7a: адреса місяця → якір, без перемикача', () => {
  it(
    'рік: ?periodKey жовтня веде на якір року; «Year» є, меню «Calculated: <місяць>» і вибору періоду немає',
    async () => {
      show('/documents/1?periodKey=209910', { layout: 'YearWithMonthColumns' });

      await waitFor(() => expect(search()).toBe(`?periodKey=${String(Anchor)}`), { timeout: SlowEnvTimeout });
      await actionsRow();
      expect(await screen.findByTestId('doc-cr-year', {}, { timeout: SlowEnvTimeout })).toBeTruthy();
      expect(screen.queryByTestId('doc-cr-month')).toBeNull();
      expect(document.querySelector('.ecr-period-anchor')).toBeNull();
      expect(requested).toContain(`/api/v1/documents/1/tables?periodKey=${String(Anchor)}`);
    },
    SlowEnvTimeout,
  );

  it(
    'копія на місяць: ?periodKey жовтня лишається жовтнем, вибір періоду є',
    async () => {
      show('/documents/1?periodKey=209910', { layout: 'PerMonthCopies' });

      await actionsRow();
      await waitFor(() => expect(document.querySelector('.ecr-period-anchor')).not.toBeNull(), { timeout: SlowEnvTimeout });
      expect(search()).toBe('?periodKey=209910');
      expect(requested).not.toContain(`/api/v1/documents/1/tables?periodKey=${String(Anchor)}`);
    },
    SlowEnvTimeout,
  );

  it(
    'рік: запасний перехід (період відхилено) веде на ЯКІР останнього року, не на його місяць',
    async () => {
      show(`/documents/1?periodKey=${String(Anchor)}`, { layout: 'YearWithMonthColumns', rejectAll: true });

      const go = await screen.findByTestId('period-recovery-go', undefined, { timeout: SlowEnvTimeout });
      expect(go.textContent).toContain(`period=${formatPeriodKey(210001)}`);
    },
    SlowEnvTimeout,
  );

  it(
    'копія на місяць: запасний перехід — на останній місяць, як і раніше',
    async () => {
      show(`/documents/1?periodKey=${String(Anchor)}`, { layout: 'PerMonthCopies', rejectAll: true });

      const go = await screen.findByTestId('period-recovery-go', undefined, { timeout: SlowEnvTimeout });
      expect(go.textContent).toContain(`period=${formatPeriodKey(210002)}`);
    },
    SlowEnvTimeout,
  );
});

describe('DocumentPage «рік» L7b: Issues за всі місяці року', () => {
  it(
    'рік: перевірка читається з year=true — видно зауваження жовтня й вересня',
    async () => {
      show(`/documents/1?periodKey=${String(Anchor)}`, { layout: 'YearWithMonthColumns' });

      expect(await issuesCount()).toBe('2');
      expect(requested).toContain(`/api/v1/documents/1/validation?periodKey=${String(Anchor)}&year=true`);
    },
    SlowEnvTimeout,
  );

  it(
    'рік: «Validate» не підміняє річний перелік прогоном якоря — перечитує рік (зауваження жовтня лишаються)',
    async () => {
      show(`/documents/1?periodKey=${String(Anchor)}`, { layout: 'YearWithMonthColumns' });

      expect(await issuesCount()).toBe('2');
      const yearReads = (): number => requested.filter((url) => url.includes('/validation?') && url.includes('year=true')).length;
      const before = yearReads();
      const row = await actionsRow();
      fireEvent.click(await within(row).findByRole('button', { name: '⟦document.validate⟧' }, { timeout: SlowEnvTimeout }));

      await waitFor(() => expect(requested.some((url) => url.includes('/validate'))).toBe(true), { timeout: SlowEnvTimeout });
      await waitFor(() => expect(yearReads()).toBeGreaterThan(before), { timeout: SlowEnvTimeout });
      expect(await issuesCount()).toBe('2');
    },
    SlowEnvTimeout,
  );

  it(
    'копія на місяць: перевірка одного періоду, без year=true',
    async () => {
      show(`/documents/1?periodKey=${String(Anchor)}`, { layout: 'PerMonthCopies' });

      expect(await issuesCount()).toBe('0');
      expect(requested.some((url) => url.includes('/validation') && url.includes('year=true'))).toBe(false);
    },
    SlowEnvTimeout,
  );
});

describe('DocumentPage «рік» L7a: панель результатів і банер замка', () => {
  it(
    'рік: панелі «Calculation results» немає (числа — у місячних колонках)',
    async () => {
      show(`/documents/1?periodKey=${String(Anchor)}`, { layout: 'YearWithMonthColumns', permissions: ['Document.View', 'Calculation.View'] });

      const row = await actionsRow();
      await within(row).findByRole('button', { name: /workflow\.submitPeriod/ }, { timeout: SlowEnvTimeout });
      expect(screen.queryByTestId('calc-stub')).toBeNull();
    },
    SlowEnvTimeout,
  );

  it(
    'копія на місяць: панель «Calculation results» на місці',
    async () => {
      show(`/documents/1?periodKey=${String(Anchor)}`, { layout: 'PerMonthCopies', permissions: ['Document.View', 'Calculation.View'] });

      expect(await screen.findByTestId('calc-stub', {}, { timeout: SlowEnvTimeout })).toBeTruthy();
    },
    SlowEnvTimeout,
  );

  it(
    'рік: увесь рік закрито — банер називає «Year 2099», а не січень',
    async () => {
      show(`/documents/1?periodKey=${String(Anchor)}`, { layout: 'YearWithMonthColumns', state: 'Closed' });

      const banner = await screen.findByText(/document\.lock\.periodClosed/, {}, { timeout: SlowEnvTimeout });
      // ⚠ Кнопка «Reopen <місяць>» поруч — L7c (вибір місяця відкриття), тут лише текст причини.
      expect(banner.textContent).toContain('periodClosed (period=⟦calcResults.yearChip (year=2099)⟧)');
      expect(banner.textContent).not.toContain(`periodClosed (period=${formatPeriodKey(Anchor)}`);
    },
    SlowEnvTimeout,
  );

  it(
    'копія на місяць: закритий місяць — банер називає місяць',
    async () => {
      show(`/documents/1?periodKey=${String(Anchor)}`, { layout: 'PerMonthCopies', state: 'Closed' });

      const banner = await screen.findByText(/document\.lock\.periodClosed/, {}, { timeout: SlowEnvTimeout });
      expect(banner.textContent).toContain(`periodClosed (period=${formatPeriodKey(Anchor)})`);
      expect(banner.textContent).not.toContain('calcResults.yearChip');
    },
    SlowEnvTimeout,
  );
});

/**
 * ✎ LAND-YEAR L7a (координатор 17.11): документ «рік» відкриває закритий МІСЯЦЬ для себе з шапки — вибір місяця
 * («Reopen a month for this document…»); лише з правом `Period.Reopen` і лише за наявності закритого місяця року.
 * Копія на місяць — без вибору (банер закритого місяця, як було).
 */
describe('DocumentPage «рік» L7a: відкриття місяця для документа', () => {
  it(
    'рік, закриті місяці, право Period.Reopen: у шапці є вибір місяця',
    async () => {
      show(`/documents/1?periodKey=${String(Anchor)}`, {
        layout: 'YearWithMonthColumns', state: 'Closed', permissions: ['Document.View', 'Period.Reopen'],
      });

      expect(await screen.findByTestId('year-month-reopen', {}, { timeout: SlowEnvTimeout })).toBeTruthy();
    },
    SlowEnvTimeout,
  );

  it(
    'рік без права Period.Reopen: вибору місяця немає',
    async () => {
      show(`/documents/1?periodKey=${String(Anchor)}`, { layout: 'YearWithMonthColumns', state: 'Closed' });

      await screen.findByText(/document\.lock\.periodClosed/, {}, { timeout: SlowEnvTimeout });
      expect(screen.queryByTestId('year-month-reopen')).toBeNull();
    },
    SlowEnvTimeout,
  );

  it(
    'рік без закритих місяців: вибору місяця немає',
    async () => {
      show(`/documents/1?periodKey=${String(Anchor)}`, { layout: 'YearWithMonthColumns', permissions: ['Document.View', 'Period.Reopen'] });

      await actionsRow();
      expect(screen.queryByTestId('year-month-reopen')).toBeNull();
    },
    SlowEnvTimeout,
  );

  it(
    'копія на місяць з правом і закритим місяцем: вибору місяця немає (дія — у банері, як було)',
    async () => {
      show(`/documents/1?periodKey=${String(Anchor)}`, { layout: 'PerMonthCopies', state: 'Closed', permissions: ['Document.View', 'Period.Reopen'] });

      await screen.findByText(/document\.lock\.periodClosed/, {}, { timeout: SlowEnvTimeout });
      expect(screen.queryByTestId('year-month-reopen')).toBeNull();
    },
    SlowEnvTimeout,
  );
});

/**
 * ✎ L7c (F13 AU до L7ab): банер «Reopened for this document until …» документа «рік» показує КОЖЕН відкритий місяць
 * року з картки (`reopenedPeriods`), а не лише якір; банер якоря (GET `/period-reopen` січня) у «рік» не малюється.
 * Мутації (локально): гілка `yearLayout` у DocumentPage → завжди `periodReopen.banner` — тест 1 червоний (немає
 * переліку, є банер якоря); банер року без `periods.map` (лише перший) — тест 1 червоний.
 */
// ✎ b14 doc-notices-compact: банер-список замінено чипом у шапці (`ReopenedMonthsChip`); правило «кожен місяць, не лише
// якір» лишається — тепер у описі чипа.
describe('DocumentPage «рік» L7c: відкриті місяці року — чип у шапці', () => {
  it(
    'рік: усі відкриті місяці року з картки, банера якоря немає',
    async () => {
      show(`/documents/1?periodKey=${String(Anchor)}`, {
        layout: 'YearWithMonthColumns', state: 'Closed', anchorActive: true,
        reopened: [
          { periodKey: 209903, openUntil: '2099-12-20T18:00:00Z' },
          { periodKey: 209907, openUntil: '2099-12-21T18:00:00Z' },
        ],
      });

      // ✎ b14 doc-notices-compact: замість банера-списку — чип у шапці; два несуміжні місяці — два діапазони, перелік — в описі.
      const chip = await screen.findByTestId('document-reopened-chip', {}, { timeout: SlowEnvTimeout });
      expect(chip.getAttribute('data-reopened-ranges')).toBe('2');
      const list = document.getElementById(chip.getAttribute('aria-describedby') ?? '');
      expect(list?.querySelectorAll('li')).toHaveLength(2);
      expect(screen.queryByTestId('document-year-reopened')).toBeNull();
      expect(screen.queryByTestId('document-period-reopened')).toBeNull();
    },
    SlowEnvTimeout,
  );

  it(
    'рік без відкритих місяців: жодного банера відкриття',
    async () => {
      show(`/documents/1?periodKey=${String(Anchor)}`, { layout: 'YearWithMonthColumns', state: 'Closed', reopened: [] });

      await screen.findByText(/document\.lock\.periodClosed/, {}, { timeout: SlowEnvTimeout });
      expect(screen.queryByTestId('document-reopened-chip')).toBeNull();
      expect(screen.queryByTestId('document-period-reopened')).toBeNull();
    },
    SlowEnvTimeout,
  );

  it(
    'копія на місяць: банер місяця, як було; перелік року не малюється',
    async () => {
      show(`/documents/1?periodKey=${String(Anchor)}`, { layout: 'PerMonthCopies', state: 'Closed', anchorActive: true });

      expect(await screen.findByTestId('document-period-reopened', {}, { timeout: SlowEnvTimeout })).toBeTruthy();
      expect(screen.queryByTestId('document-reopened-chip')).toBeNull();
    },
    SlowEnvTimeout,
  );
});
