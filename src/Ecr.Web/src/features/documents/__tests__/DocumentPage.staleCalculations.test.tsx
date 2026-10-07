import type { JSX } from 'react';
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { DocumentPage } from '@/pages/DocumentPage';

/**
 * Лінія B: застарілі числа методологій — бейдж у шапці й пункт «Recalculate calculations» у «More»;
 * завершений перерахунок перечитує числа, і бейдж зникає.
 *
 * ⚠ Панель чисел замінено заглушкою: перевіряється саме шапка, а запит чисел — той самий ключ, що в панелі.
 */
vi.mock('@/features/grid/DocumentGrid', () => ({
  DocumentGrid: (): JSX.Element => <div data-testid="grid-stub" />,
}));

vi.mock('@/features/methodologies/CalculationResultsPanel', () => ({
  CalculationResultsPanel: (): JSX.Element => <div data-testid="calc-stub" />,
}));

vi.mock('@mantine/notifications', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@mantine/notifications')>();

  return { ...actual, notifications: { ...actual.notifications, show: vi.fn(), hide: vi.fn() } };
});

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

const result = (isStale: boolean): unknown => ({
  rowKey: 'R1',
  outputCode: 'OUT',
  value: 1,
  unitId: 1,
  isStale,
  changedRegistries: [],
});

/** `staleSequence` — відповіді `calculation-results` по черзі; остання повторюється. */
function mockFetch(
  permissions: string[],
  staleSequence: boolean[],
  recalcRefusal = false,
): { posts: () => number } {
  let calcReads = 0;
  let posts = 0;

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);

      if (url.includes('/api/v1/me')) {
        return jsonResponse({
          denies: [],
          grants: {},
          isSimulation: false,
          language: 'en',
          mustChangePassword: false,
          permissions,
          simulatedForUserId: null,
          userId: 1,
          userName: 'tester',
        });
      }

      if (url.includes('/calculation-results')) {
        const stale = staleSequence[Math.min(calcReads, staleSequence.length - 1)] ?? false;
        calcReads += 1;
        return jsonResponse([result(stale)]);
      }

      if (url.includes('/recalculate') && init?.method === 'POST') {
        posts += 1;
        if (recalcRefusal) {
          return jsonResponse(
            {
              status: 422,
              title: 'Recalculation is not allowed',
              errorCode: 'ECR-CALC-4221',
              detail: 'Period 202401 has submitted sheets: reopen the period first.',
              messageKey: 'err.ECR-CALC-4221.sheetsSubmitted',
              period: '202401',
            },
            422,
          );
        }
        return jsonResponse({ jobId: 'job-1', documentId: 1, periodKey: 202401 }, 202);
      }

      if (url.includes('/api/v1/jobs/')) return jsonResponse({ state: 'Succeeded' });

      if (url.includes('/validation')) {
        return jsonResponse({ title: 'Not found', status: 404, detail: 'x', errorCode: 'ECR-DOC-0404' }, 404);
      }

      if (url.includes('/tables/status')) return jsonResponse([]);

      if (url.includes('/tables')) {
        return jsonResponse([
          {
            allowsDynamicRows: false,
            maxDynamicRows: null,
            sheetCode: 'GEN',
            sheetDefId: 1,
            sheetNameL10n: { values: { en: 'GEN' } },
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
          sheetStates: { GEN: 'Draft' },
          // Сервер (resultsStale) і числа методологій кажуть одне й те саме: перший прогін - застарілий.
          resultsStale: staleSequence[0] === true,
          resultsStaleSince: staleSequence[0] === true ? '2026-10-07T10:00:00Z' : null,
        });
      }

      return jsonResponse(null);
    }),
  );

  return { posts: () => posts };
}

function show(
  permissions: string[],
  staleSequence: boolean[],
  recalcRefusal = false,
): { posts: () => number } {
  const probe = mockFetch(permissions, staleSequence, recalcRefusal);
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider>
      <MemoryRouter initialEntries={['/documents/1?periodKey=202401']}>
        <QueryClientProvider client={client}>
          <Routes>
            <Route path="/documents/:id" element={<DocumentPage />} />
          </Routes>
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );

  return probe;
}

const Slow = 60_000;
const BadgeId = 'document-methodology-stale';
const BannerId = 'document-stale-results';
const Recalculated = { name: /⟦workflow\.recalculate⟧/ };
const RecalculatedCalc = { name: /⟦workflow\.recalculateCalculations⟧/ };
const MoreButton = { name: '⟦document.moreActions⟧' };
const ValidateButton = { name: '⟦document.validate⟧' };

describe('DocumentPage: застарілі числа методологій', () => {
  // ⛔ Лінві чанки сторінки - у кеш модулів ДО першого рендера (як у DocumentPage.staleValidation): інакше
  // повторні спроби меж <Suspense> крутяться під синхронним act() кліку, а наприкінці тесту чанк
  // довантажується в закритий раннер ("Vite module runner has been closed").
  beforeAll(async () => {
    await Promise.all([
      import('@/features/documents/DocumentHeaderPanel'),
      import('@/features/documents/DocumentVersionCompare'),
      import('@/features/documents/ValidationPanel'),
      import('@/features/grid/RestoreEditsBanner'),
      import('@/features/grid/SheetTables'),
      import('@/features/grid/cellNavigation'),
      import('@/features/import/ImportPanel'),
      import('@/features/projects/allProjects'),
      import('@/features/workflow/WorkflowHistory'),
      import('@/shared/ui/PeriodPicker'),
    ]);
  }, 120_000);

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it(
    'застаріло — одне сповіщення (банер), без бейджа в шапці; пункт перерахунку в «More» — «Recalculate calculations»',
    async () => {
      show(['Document.View', 'Calculation.View'], [true]);

      await screen.findByTestId(BannerId, undefined, { timeout: Slow });
      fireEvent.click(screen.getByRole('button', MoreButton));
      expect(await screen.findByRole('menuitem', RecalculatedCalc, { timeout: Slow })).toBeDefined();
      // ⛔ Банер і бейдж не показуються одночасно: дубль двох повідомлень про одне й те саме.
      expect(screen.queryByTestId(BadgeId)).toBeNull();
      expect(screen.getAllByTestId(BannerId)).toHaveLength(1);
    },
    Slow,
  );

  it(
    'числа свіжі — ні банера, ні бейджа, пункт зі звичайною назвою',
    async () => {
      show(['Document.View', 'Calculation.View'], [false]);

      await screen.findByRole('button', ValidateButton, { timeout: Slow });
      // ⛔ Спершу дочекатися, що числа прочитано, інакше «немає» було б правдою від нічого.
      await waitFor(() => {
        expect(vi.mocked(fetch).mock.calls.some(([u]) => String(u).includes('/calculation-results'))).toBe(true);
      });
      fireEvent.click(screen.getByRole('button', MoreButton));
      expect(await screen.findByRole('menuitem', Recalculated)).toBeDefined();
      expect(screen.queryByTestId(BannerId)).toBeNull();
      expect(screen.queryByTestId(BadgeId)).toBeNull();
      expect(screen.queryByRole('menuitem', RecalculatedCalc)).toBeNull();
    },
    Slow,
  );

  it(
    'без права Calculation.View — числа не читаються, пункт зі звичайною назвою',
    async () => {
      show(['Document.View'], [true]);

      await screen.findByRole('button', ValidateButton, { timeout: Slow });
      fireEvent.click(screen.getByRole('button', MoreButton));
      expect(await screen.findByRole('menuitem', Recalculated)).toBeDefined();
      expect(screen.queryByRole('menuitem', RecalculatedCalc)).toBeNull();
      expect(screen.queryByTestId(BadgeId)).toBeNull();
      expect(vi.mocked(fetch).mock.calls.some(([u]) => String(u).includes('/calculation-results'))).toBe(false);
    },
    Slow,
  );

  it(
    'перерахунок із More завершено — числа перечитано, пункт знову зі звичайною назвою',
    async () => {
      const probe = show(['Document.View', 'Calculation.View'], [true, false]);

      await screen.findByTestId(BannerId, undefined, { timeout: Slow });
      fireEvent.click(screen.getByRole('button', MoreButton));
      fireEvent.click(await screen.findByRole('menuitem', RecalculatedCalc, { timeout: Slow }));
      await waitFor(() => expect(probe.posts()).toBe(1), { timeout: Slow });

      // Числа перечитано (другий запит calculation-results) - і лише тоді дивимось на меню.
      await waitFor(
        () => {
          const reads = vi.mocked(fetch).mock.calls.filter(([u]) => String(u).includes('/calculation-results'));
          expect(reads.length).toBeGreaterThanOrEqual(2);
        },
        { timeout: Slow },
      );
      fireEvent.click(await screen.findByRole('button', MoreButton));
      expect(await screen.findByRole('menuitem', Recalculated, { timeout: Slow })).toBeDefined();
      expect(screen.queryByRole('menuitem', RecalculatedCalc)).toBeNull();
    },
    Slow,
  );

  it(
    '422 ECR-CALC-4221 (є подані аркуші) — помилка з поясненням, а не «перераховано»; банер лишається',
    async () => {
      vi.mocked(notifications.show).mockClear();
      const probe = show(['Document.View', 'Calculation.View'], [true], true);

      await screen.findByTestId(BannerId, undefined, { timeout: Slow });
      fireEvent.click(screen.getByRole('button', MoreButton));
      fireEvent.click(await screen.findByRole('menuitem', RecalculatedCalc, { timeout: Slow }));

      await waitFor(() => expect(probe.posts()).toBe(1), { timeout: Slow });
      await waitFor(
        () => expect(vi.mocked(notifications.show).mock.calls.some(([o]) => o.color === 'statusError')).toBe(true),
        { timeout: Slow },
      );

      const shown = vi.mocked(notifications.show).mock.calls.map(([o]) => o);
      // ⛔ Мутаційний доказ: покажи «Recalculated»/«queued» на відмову — тут червоне.
      expect(shown.some((o) => o.color === 'statusSuccess')).toBe(false);
      expect(JSON.stringify(shown.find((o) => o.color === 'statusError'))).toContain('has submitted sheets: reopen the period first');
      expect(screen.getByTestId(BannerId)).toBeDefined();
    },
    Slow,
  );
});
