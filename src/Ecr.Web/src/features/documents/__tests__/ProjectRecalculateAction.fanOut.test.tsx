import { describe, it, expect, vi, afterEach } from 'vitest';
import { configure, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { ProjectRecalculateAction } from '@/features/documents/ProjectRecalculateAction';
import { ProjectRecalcBanner } from '@/features/recalc/ProjectRecalcBanner';
import { testTheme } from '@/test/render';

/**
 * ✎ UAT recalc-progress (08.11, a1–a3, a6; a-P3): тост «finished: figures up to date» з'являвся ~1 с після розкладу,
 * поки 92 документні задачі ще рахувались ~20 хв.
 *
 * ⛔ Доказ: після підтвердження — лише «Queued…»; поки батько `FannedOut`, банер каже «Recalculating … k of N», а
 * «Finished» не з'являється ніде; «Finished: N recalculated, m skipped (approved), f failed» — лише коли сервер
 * каже, що діти завершились. З помилками — «Finished with errors» і перелік провалених документів із Retry.
 * Мутація «`progressOf` без `effectiveState`» дає «Finished» уже на першому читанні.
 */

vi.mock('@mantine/notifications', () => ({ notifications: { show: vi.fn(), hide: vi.fn() } }));

configure({ asyncUtilTimeout: 20_000 });

interface Fan {
  total: number;
  queued: number;
  running: number;
  succeeded: number;
  failed: number;
  skippedSheets: number;
}

interface ServerState {
  queued: boolean;
  /** Батько ще в черзі: розкладу немає (state: Queued, fanOut: null). */
  parentQueued: boolean;
  effectiveState: string;
  fanOut: Fan;
  listReads: number;
}

function respond(): ServerState {
  const state: ServerState = {
    queued: false,
    parentQueued: false,
    effectiveState: 'FannedOut',
    fanOut: { total: 3, queued: 2, running: 0, succeeded: 1, failed: 0, skippedSheets: 2 },
    listReads: 0,
  };

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const json = (body: unknown, code = 200): Response =>
        new Response(JSON.stringify(body), { status: code, headers: { 'Content-Type': 'application/json' } });

      if (init?.method === 'POST') {
        state.queued = true;
        return json({ jobId: 'IRecalculationJob~project7-year~1', skipped: [] }, 202);
      }

      if (url.includes('parentJobId=')) {
        return json([
          {
            jobId: 'IRecalculationJob~doc51-year~1',
            jobCode: 'Ecr.Application.Ports.IRecalculationJob',
            state: 'Failed',
            percent: 40,
            updatedAt: '2026-11-08T09:00:00Z',
            startedAt: '2026-11-08T09:00:00Z',
            documentId: 51,
            errorCode: null,
            fanOutParentJobId: 'IRecalculationJob~project7-year~1',
          },
        ]);
      }

      if (url.includes('/api/v1/jobs?')) {
        state.listReads += 1;
        if (!state.queued) return json([]);

        return json([
          {
            jobId: 'IRecalculationJob~project7-year~1',
            jobCode: 'Ecr.Application.Ports.IRecalculationJob',
            state: state.parentQueued ? 'Queued' : 'Succeeded',
            percent: state.parentQueued ? 0 : 100,
            updatedAt: new Date().toISOString(),
            startedAt: new Date().toISOString(),
            documentId: null,
            projectId: 7,
            projectCode: 'PRJ-7',
            effectiveState: state.parentQueued ? 'Queued' : state.effectiveState,
            fanOut: state.parentQueued ? null : state.fanOut,
          },
        ]);
      }

      if (url.includes('/periods')) {
        return json({
          projectId: 7,
          periodKind: 'Monthly',
          currentPeriodMode: 'Auto',
          timeZoneId: 'Asia/Aqtau',
          policy: { id: 1, code: 'STD', openOffsetDays: 0, graceOffsetDays: 15, hardCloseOffsetDays: 45, yearGraceOffsetDays: 45 },
          periods: [
            // ⚠ Збережений `Open`, межа блокування давно минула — у діалозі місяць названо «пропускається».
            { id: 1, periodKey: 202601, year: 2026, sequence: 1, state: 'Open', startsAt: '2026-01-01T00:00:00+05:00', endsAt: '2026-03-17T00:00:00+05:00', graceEndsAt: '2026-02-15T00:00:00+05:00', reopenedUntil: null, isCurrent: false },
            { id: 3, periodKey: 202609, year: 2026, sequence: 9, state: 'Grace', startsAt: '2026-09-01T00:00:00+05:00', endsAt: '2099-11-15T00:00:00+05:00', graceEndsAt: '2026-10-15T00:00:00+05:00', reopenedUntil: null, isCurrent: false },
            { id: 4, periodKey: 202610, year: 2026, sequence: 10, state: 'Open', startsAt: '2026-10-01T00:00:00+05:00', endsAt: '2099-12-15T00:00:00+05:00', graceEndsAt: '2026-11-15T00:00:00+05:00', reopenedUntil: null, isCurrent: false },
            { id: 5, periodKey: 202611, year: 2026, sequence: 11, state: 'Open', startsAt: '2026-11-01T00:00:00+05:00', endsAt: '2099-01-15T00:00:00+05:00', graceEndsAt: '2026-12-15T00:00:00+05:00', reopenedUntil: null, isCurrent: false },
            { id: 2, periodKey: 202612, year: 2026, sequence: 12, state: 'Scheduled', startsAt: '2026-12-01T00:00:00+05:00', endsAt: '2099-02-14T00:00:00+05:00', graceEndsAt: '2099-01-15T00:00:00+05:00', reopenedUntil: null, isCurrent: true },
          ],
        });
      }

      return json(null);
    }),
  );

  return state;
}

function show(count: number | null = 92): void {
  render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter initialEntries={['/?projectId=7']}>
        <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
          <ProjectRecalcBanner projectId={7} />
          <ProjectRecalculateAction project={{ id: 7, code: 'PRJ-7', documentCount: count ?? undefined }} />
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

function shown(): string[] {
  return vi.mocked(notifications.show).mock.calls.map(([n]) => String(n.message));
}

async function startRecalc(): Promise<HTMLElement> {
  const user = userEvent.setup();

  await user.click(await screen.findByRole('button', { name: '⟦workflow.recalculate⟧' }));
  const dialog = await screen.findByRole('dialog', { name: '⟦periods.recalcTitle (code=PRJ-7)⟧' });
  // ✎ P2 живого проходу 08.11: реальна область — відкриті місяці (суцільний відрізок; заблокований січень і запланований
  // грудень поза нею) і число документів; старого «Every document of every period» немає.
  await within(dialog).findByText(/recalcProgress\.confirmScope \(months=September–November 2026, documents=92, documentCount=⟦recalcProgress\.documentCount\.other \(count=92\)⟧\)/);
  expect(within(dialog).queryByText(/Every document/)).toBeNull();
  await user.click(within(dialog).getByRole('button', { name: '⟦workflow.recalculate⟧' }));

  return dialog;
}

const Slow = 120_000;

afterEach(() => {
  vi.unstubAllGlobals();
  vi.mocked(notifications.show).mockClear();
  globalThis.localStorage?.clear();
});

describe('ProjectRecalculateAction + ProjectRecalcBanner: «finished» лише коли завершились усі документи (a-P3)', () => {
  it(
    'Queued → Recalculating k of N (без «Finished») → Finished з лічильниками',
    async () => {
      const state = respond();
      show();

      await startRecalc();

      // a1: «Queued…», ніколи «finished».
      await waitFor(() => expect(shown()).toContain('⟦recalcProgress.queuedDocumentsToast (project=PRJ-7, total=92, documentCount=⟦recalcProgress.documentCount.other (count=92)⟧)⟧'));
      expect(shown().some((m) => /finished/i.test(m))).toBe(false);

      // a2: банер із прогресом; опитування триває.
      expect(
        await screen.findByText(
          '⟦recalcProgress.running (project=PRJ-7, done=1, total=3, documentCount=⟦recalcProgress.documentCount.other (count=3)⟧, skipped=2, failed=0)⟧',
        ),
      ).toBeTruthy();
      const reads = state.listReads;
      await waitFor(() => expect(state.listReads).toBeGreaterThan(reads), { timeout: 10_000 });
      expect(screen.queryByText(/recalcProgress\.finished/)).toBeNull();
      expect(screen.getByRole('button', { name: '⟦workflow.recalcRunning⟧' })).toBeTruthy();

      // a3: усі діти завершились.
      state.effectiveState = 'Succeeded';
      state.fanOut = { total: 3, queued: 0, running: 0, succeeded: 3, failed: 0, skippedSheets: 2 };

      expect(
        await screen.findByText(
          '⟦recalcProgress.finished (project=PRJ-7, recalculated=3, skipped=2, failed=0)⟧',
          {},
          { timeout: 15_000 },
        ),
      ).toBeTruthy();
    },
    Slow,
  );

  it(
    'a1: до розкладу тост і банер кажуть «Queued N documents» з реальним N (documentCount проєкту)',
    async () => {
      // ⛔ До фіксу: тост «Recalculation of X queued. Progress is shown above the list.» без N, а банер, поки батько
      // в черзі (fanOut ще немає), — «Queued recalculation of X…» без N.
      const state = respond();
      state.parentQueued = true;
      show(92);

      await startRecalc();

      await waitFor(() => expect(shown()).toContain('⟦recalcProgress.queuedDocumentsToast (project=PRJ-7, total=92, documentCount=⟦recalcProgress.documentCount.other (count=92)⟧)⟧'));
      expect(await screen.findByText('⟦recalcProgress.queuedDocuments (project=PRJ-7, total=92, documentCount=⟦recalcProgress.documentCount.other (count=92)⟧)⟧')).toBeTruthy();
      expect(screen.queryByText(/recalcProgress\.queuedProject/)).toBeNull();
    },
    Slow,
  );

  it(
    'a1: без числа документів — тост без N (не вигадане число)',
    async () => {
      const state = respond();
      state.parentQueued = true;
      show(null);

      const user = userEvent.setup();
      await user.click(await screen.findByRole('button', { name: '⟦workflow.recalculate⟧' }));
      const dialog = await screen.findByRole('dialog', { name: '⟦periods.recalcTitle (code=PRJ-7)⟧' });
      await user.click(within(dialog).getByRole('button', { name: '⟦workflow.recalculate⟧' }));

      await waitFor(() => expect(shown()).toContain('⟦recalcProgress.queuedToast (project=PRJ-7)⟧'));
    },
    Slow,
  );

  it(
    'з помилками — «Finished with errors» і перелік провалених документів із Retry (a6)',
    async () => {
      const state = respond();
      state.effectiveState = 'SucceededWithErrors';
      state.fanOut = { total: 3, queued: 0, running: 0, succeeded: 2, failed: 1, skippedSheets: 0 };
      show();

      await startRecalc();

      expect(
        await screen.findByText(
          '⟦recalcProgress.finishedWithErrors (project=PRJ-7, recalculated=2, skipped=0, failed=1)⟧',
        ),
      ).toBeTruthy();

      await userEvent.setup().click(screen.getByRole('button', { name: '⟦recalcProgress.showFailed (failed=1)⟧' }));

      expect(await screen.findByText('⟦jobs.openDocument (id=51)⟧')).toBeTruthy();
      expect(screen.getByRole('button', { name: '⟦jobs.restart⟧' })).toBeTruthy();
    },
    Slow,
  );
});
