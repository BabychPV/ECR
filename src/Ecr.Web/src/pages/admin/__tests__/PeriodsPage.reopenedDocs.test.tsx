import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { PeriodsPage } from '@/pages/admin/PeriodsPage';
import { queryKeys } from '@/api/queryKeys';
import { siteMomentText } from '@/shared/siteMoment';
import { testTheme } from '@/test/render';

/**
 * Приймальна i4: «Projects → Periods tab: January shows "Locked · 1 document reopened until …" with the document name
 * and a "Close now" action».
 *
 * ⚠ Каталог тут НЕ завантажується: `t()` повертає `⟦ключ (param=val)⟧`, тож доказ множини — у категорії ключа
 * (`.one` / `.other`) і в `count=`; строк — у поясі майданчика (`siteMomentText`, Europe/Kyiv).
 */

const project = {
  id: 7,
  code: 'PRJ-7',
  status: 'Active' as const,
  periodKind: 'Monthly' as const,
  periodCount: 2,
  currentPeriodId: null,
  timeZoneId: 'Europe/Kyiv',
};

const calendar = {
  projectId: 7,
  periodKind: 'Monthly' as const,
  currentPeriodMode: 'Auto' as const,
  timeZoneId: 'Europe/Kyiv',
  policy: { id: 1, code: 'ECR-Standard', openOffsetDays: 0, graceOffsetDays: 15, hardCloseOffsetDays: 45, yearGraceOffsetDays: 45 },
  periods: [
    { id: 1, periodKey: 202601, year: 2026, sequence: 1, startsAt: '2026-01-01T00:00:00+02:00', endsAt: '2026-03-17T00:00:00+02:00', state: 'Closed', graceEndsAt: '2026-02-15T00:00:00+02:00', reopenedUntil: null, isCurrent: false },
    { id: 2, periodKey: 202602, year: 2026, sequence: 2, startsAt: '2026-02-01T00:00:00+02:00', endsAt: '2099-04-15T00:00:00+03:00', state: 'Open', graceEndsAt: '2099-03-15T00:00:00+02:00', reopenedUntil: null, isCurrent: true },
  ],
};

const UntilA = '2099-01-10T20:00:00Z';
const UntilB = '2099-01-11T08:30:00Z';

const docA = { id: 11, documentId: 501, documentName: 'DOC-501', periodKey: 202601, openUntil: UntilA, reason: 'Лист №17', openedByName: 'Admin' };
const docB = { id: 12, documentId: 502, documentName: 'DOC-502', periodKey: 202601, openUntil: UntilB, reason: 'Лист №18', openedByName: 'Admin' };
// Виняток ВІДКРИТОГО місяця — рядок Open його не показує (лише Locked).
const docOpenMonth = { id: 13, documentId: 503, documentName: 'DOC-503', periodKey: 202602, openUntil: UntilA, reason: 'x', openedByName: null };

const deletes: string[] = [];

function mockFetch(items: unknown[], canClose = true): void {
  deletes.length = 0;
  let current = [...items];
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const json = (body: unknown): Response =>
        new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });

      if (init?.method === 'DELETE') {
        deletes.push(url);
        current = current.filter((item) => !url.includes(`/documents/${String((item as { documentId: number }).documentId)}/`));
        return new Response(null, { status: 204 });
      }

      if (url.includes('/api/v1/me')) {
        return json({
          denies: [],
          grants: { 'Project:7': 'Manage' },
          isSimulation: false,
          language: 'en',
          mustChangePassword: false,
          permissions: ['Project.Manage', 'Period.Reopen'],
          simulatedForUserId: null,
          userId: 1,
          userName: 'tester',
        });
      }

      if (url.includes('/api/v1/projects/7/document-reopens')) return json({ canClose, items: current });
      if (url.includes('/api/v1/projects') && url.includes('/periods')) return json(calendar);
      if (url.includes('/api/v1/projects')) return json({ items: [project], nextCursor: null, totalCount: 1 });

      return json(null);
    }),
  );
}

function show(client = new QueryClient({ defaultOptions: { queries: { retry: false } } })): QueryClient {
  render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter initialEntries={['/projects/7/periods']}>
        <QueryClientProvider client={client}>
          <PeriodsPage projectId={7} />
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );

  return client;
}

afterEach(() => {
  vi.unstubAllGlobals();
});

const Slow = 300_000;

async function row(key: number): Promise<HTMLElement> {
  await screen.findByText((_, el) => el?.getAttribute('data-period-key') === String(key), {}, { timeout: 60_000 });

  return document.querySelector<HTMLElement>(`[data-period-row="${String(key)}"]`) as HTMLElement;
}

describe('PeriodsPage: Locked month reopened for documents (i4)', () => {
  it(
    'January: «Locked · 1 document reopened until …» in the site zone, the document link and Close now',
    async () => {
      mockFetch([docA, docOpenMonth]);
      show();
      const jan = await row(202601);

      const summary = await within(jan).findByTestId('period-reopened-summary');
      const until = siteMomentText(UntilA, 'Europe/Kyiv');
      expect(until).not.toBeNull();
      // ⛔ Мутація «без formatCount — завжди .other» дає тут `.other`, тобто «1 documents».
      expect(summary.textContent).toBe(`⟦periods.reopenedDocs.summary.one (count=1, until=${String(until)})⟧`);

      const link = within(jan).getByRole('link', { name: 'DOC-501' });
      expect(link.getAttribute('href')).toBe('/documents/501?periodKey=202601');
      expect(within(jan).getByRole<HTMLButtonElement>('button', { name: '⟦periods.reopenedDocs.closeNow⟧' }).disabled).toBe(false);
      expect(within(jan).queryByTestId('period-reopened-denied')).toBeNull();

      // Відкритий місяць винятків не показує.
      const feb = await row(202602);
      expect(within(feb).queryByTestId('period-reopened-summary')).toBeNull();
    },
    Slow,
  );

  it(
    'two documents: plural «.other» with count=2 and the LATEST until; each document has its own until',
    async () => {
      mockFetch([docA, docB]);
      show();
      const jan = await row(202601);

      const summary = await within(jan).findByTestId('period-reopened-summary');
      expect(summary.textContent).toBe(
        `⟦periods.reopenedDocs.summary.other (count=2, until=${String(siteMomentText(UntilB, 'Europe/Kyiv'))})⟧`,
      );
      expect(within(jan).getAllByRole('button', { name: '⟦periods.reopenedDocs.closeNow⟧' })).toHaveLength(2);
      expect(jan.textContent).toContain(`⟦periods.reopenedDocs.itemUntil (until=${String(siteMomentText(UntilA, 'Europe/Kyiv'))})⟧`);
    },
    Slow,
  );

  it(
    'Close now: confirm first, then DELETE, and the row is plain Locked again without a reload; keys invalidated',
    async () => {
      mockFetch([docA]);
      const user = userEvent.setup();
      const client = show();
      const invalidate = vi.spyOn(client, 'invalidateQueries');
      const jan = await row(202601);

      await user.click(await within(jan).findByRole('button', { name: '⟦periods.reopenedDocs.closeNow⟧' }));
      const dialog = await screen.findByRole('dialog', {
        name: '⟦periods.reopenedDocs.confirmTitle (period=January 2026, document=DOC-501)⟧',
      });
      // Підтвердження — до будь-якого запиту.
      expect(deletes).toEqual([]);

      await user.click(within(dialog).getByRole('button', { name: '⟦periods.reopenedDocs.closeNow⟧' }));
      await waitFor(() => expect(deletes).toEqual(['/api/v1/documents/501/period-reopen?periodKey=202601']));

      // ⛔ Мутація «не інвалідувати перелік» лишає рядок «reopened» до перезавантаження.
      await waitFor(() => expect(within(jan).queryByTestId('period-reopened-summary')).toBeNull());
      expect(within(jan).getByText('⟦status.period.Closed⟧')).toBeTruthy();

      const keys = invalidate.mock.calls.map(([filters]) => JSON.stringify(filters?.queryKey));
      expect(keys).toContain(JSON.stringify(['periods', 7, 'document-reopens']));
      expect(keys).toContain(JSON.stringify(['periods', 7]));
      expect(keys).toContain(JSON.stringify(['document', 501, 'period-reopen', 202601]));
      expect(keys).toContain(JSON.stringify(queryKeys.document.ofDocument(501)));
      expect(keys).toContain(JSON.stringify(queryKeys.slices.all()));
    },
    Slow,
  );

  it(
    'without Period.Reopen: Close now is disabled and the reason is shown in words',
    async () => {
      mockFetch([docA], false);
      show();
      const jan = await row(202601);

      const button = await within(jan).findByRole<HTMLButtonElement>('button', { name: '⟦periods.reopenedDocs.closeNow⟧' });
      // ⛔ Мутація «кнопка без disabled» дає тут false.
      expect(button.disabled).toBe(true);
      expect(within(jan).getByTestId('period-reopened-denied').textContent).toBe('⟦periods.reopenedDocs.noPermission⟧');
    },
    Slow,
  );
});
