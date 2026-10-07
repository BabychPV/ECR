import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter, useLocation } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { JSX } from 'react';
import { DocumentsPage } from '@/pages/DocumentsPage';
import { latestEvents, sheetFill } from '@/features/documents/DocumentQuickLook';
import { testTheme } from '@/test/render';

/**
 * `UI-29`: швидкий перегляд документа зі списку (макет `screens-work.js` →
 * `ctx.panel('*')`, `13-docs-quicklook.png`).
 *
 * Приймання картки: 1) відкривається з «ока» і з `?panel=`; 2) аркуші зі станом і
 * заповненістю; 3) «Open document» зберігає період; 4) без історії блок не
 * показується; 5) Esc закриває (і фокус вертається на «око»).
 * Плюс P1: подія журналу прихованого аркуша не показується.
 */
const Period = 202609;
const SlowEnvTimeout = 20_000;
const TestTimeout = 60_000;

const doc = {
  id: 11,
  businessKey: 'DOC-000011',
  nameL10n: { values: { en: 'Atyrau refinery' } },
  createdAt: '2026-01-01T00:00:00Z',
  projectId: 1,
  sheetCount: 2,
  sheetStates: { GEN: 'Approved', AIR: 'Draft' },
  sheets: [
    { code: 'GEN', nameL10n: { values: { en: 'General info' } }, state: 'Approved' },
    { code: 'AIR', nameL10n: { values: { en: 'Air emissions' } }, state: 'Draft' },
  ],
  modifiedAt: '2026-10-05T11:42:00Z',
  modifiedByDisplayName: 'Aigerim Sadykova',
  errorCount: 3,
  warningCount: 0,
  hasLateEdits: false,
};

const tables = [
  { tableDefId: 1, sheetCode: 'GEN', inputCells: 4, filledCells: 4, errorCount: 0, warningCount: 0, isClosed: false, rowCount: 0 },
  { tableDefId: 2, sheetCode: 'GEN', inputCells: 2, filledCells: 2, errorCount: 0, warningCount: 0, isClosed: false, rowCount: 0 },
  { tableDefId: 3, sheetCode: 'AIR', inputCells: 9, filledCells: 3, errorCount: 3, warningCount: 0, isClosed: false, rowCount: 0 },
  // Таблиця без полів для людини (`R-13`) — у заповненість не входить.
  { tableDefId: 4, sheetCode: 'AIR', inputCells: 0, filledCells: 0, errorCount: null, warningCount: null, isClosed: false, rowCount: 0 },
];

const event = (sheetCode: string, at: string, toState: string) => ({
  action: 'Submit',
  at,
  byDisplayName: 'Marat Zhakupov',
  fromState: 'Draft',
  toState,
  reason: null,
  sheetCode,
  stepOrdinal: null,
});

const history = [
  event('GEN', '2026-10-01T09:00:00Z', 'Submitted'),
  event('GEN', '2026-10-02T09:00:00Z', 'Approved'),
  // ⛔ Аркуш, якого роль не бачить (немає в `sheets`) — у шторці його бути не може.
  event('SECRET', '2026-10-04T09:00:00Z', 'Submitted'),
  event('AIR', '2026-10-03T09:00:00Z', 'Submitted'),
  event('AIR', '2026-10-03T12:00:00Z', 'Draft'),
];

function mockFetch(historyBody: unknown = history): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      const json = (body: unknown): Response =>
        new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });

      if (url.includes('/api/v1/me')) {
        return json({
          denies: [],
          grants: {},
          isSimulation: false,
          language: 'en',
          mustChangePassword: false,
          permissions: [],
          simulatedForUserId: null,
          userId: 1,
          userName: 'tester',
        });
      }
      if (url.includes('/tables/status')) return json(tables);
      if (url.includes('/workflow/history')) return json(historyBody);
      if (url.includes('/api/v1/documents/summary')) return json({ draft: 1, submitted: 0, approved: 0, rejected: 0, withIssues: 1 });
      if (url.includes('/api/v1/documents')) return json({ items: [doc], nextCursor: null, totalCount: 1 });
      if (url.includes('/api/v1/projects')) {
        return json({ items: [{ id: 1, code: 'ATR', status: 'Active' }], nextCursor: null, totalCount: 1 });
      }

      return json(null);
    }),
  );
}

function LocationProbe(): JSX.Element {
  const location = useLocation();

  return <span data-testid="location">{location.pathname + location.search}</span>;
}

function show(url = `/?periodKey=${String(Period)}`): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter initialEntries={[url]}>
        <QueryClientProvider client={client}>
          <DocumentsPage />
          <LocationProbe />
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

/** `findByRole` по всій таблиці в jsdom повільний — шукаємо «око» за атрибутом, а ім'я звіряємо окремо. */
async function findEye(): Promise<HTMLButtonElement> {
  const eye = await waitFor(
    () => {
      const found = document.querySelector<HTMLButtonElement>('[data-quick-look="11"]');
      if (found === null) throw new Error('«око» ще не з\'явилося');
      return found;
    },
    { timeout: SlowEnvTimeout },
  );
  expect(eye.getAttribute('aria-label')).toBe('⟦documents.quickLook (key=DOC-000011)⟧');

  return eye;
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('DocumentsPage: швидкий перегляд (UI-29)', () => {
  it(
    '«око» відкриває шторку під ?panel=<id>: аркуші, стан, заповненість лише їхніх таблиць',
    async () => {
      mockFetch();
      show();
      const user = userEvent.setup();

      const eye = await findEye();
      await user.click(eye);

      expect(screen.getByTestId('location').textContent).toContain('panel=11');

      const drawer = await screen.findByRole('dialog', {}, { timeout: SlowEnvTimeout });
      expect(within(drawer).getByText('Atyrau refinery')).toBeTruthy();

      await waitFor(() => expect(drawer.querySelector('[data-quick-look-fill="2/2"]')).not.toBeNull(), {
        timeout: SlowEnvTimeout,
      });
      // AIR: 1 таблиця для людини (друга без полів), заповнена не повністю.
      expect(drawer.querySelector('[data-quick-look-sheet="AIR"] [data-quick-look-fill]')?.getAttribute('data-quick-look-fill')).toBe('0/1');
      expect(drawer.querySelector('[data-quick-look-sheet="GEN"] [data-status-state="Approved"]')).not.toBeNull();
    },
    TestTimeout,
  );

  it(
    '«Latest changes»: три найновіші події видимих аркушів; подія прихованого аркуша не показується',
    async () => {
      mockFetch();
      show(`/?periodKey=${String(Period)}&panel=11`);

      const drawer = await screen.findByRole('dialog', {}, { timeout: SlowEnvTimeout });
      await waitFor(() => expect(drawer.querySelectorAll('[data-quick-look-event]')).toHaveLength(3), {
        timeout: SlowEnvTimeout,
      });

      const text = drawer.querySelector('[data-quick-look-changes]')?.textContent ?? '';
      // ⛔ Мутаційний доказ: прибери фільтр за видимими аркушами — подія SECRET (найновіша) потрапить у перелік.
      expect(text).not.toContain('SECRET');
      expect(text).toContain('Air emissions');
      // Назва аркуша, а не код.
      expect(text).not.toContain('AIR');
    },
    TestTimeout,
  );

  it(
    'історії немає — блоку «Latest changes» немає (D15-06), а не порожній заголовок',
    async () => {
      mockFetch([]);
      show(`/?periodKey=${String(Period)}&panel=11`);

      const drawer = await screen.findByRole('dialog', {}, { timeout: SlowEnvTimeout });
      await waitFor(() => expect(drawer.querySelector('[data-quick-look-fill="2/2"]')).not.toBeNull(), {
        timeout: SlowEnvTimeout,
      });
      await new Promise((resolve) => {
        setTimeout(resolve, 50);
      });

      expect(within(drawer).queryByText('⟦documents.quickLookLatestChanges⟧')).toBeNull();
    },
    TestTimeout,
  );

  it(
    '«Open document» веде на документ ЗІ збереженим періодом',
    async () => {
      mockFetch();
      show(`/?periodKey=${String(Period)}&panel=11`);
      const user = userEvent.setup();

      const drawer = await screen.findByRole('dialog', {}, { timeout: SlowEnvTimeout });
      await user.click(await within(drawer).findByRole('button', { name: '⟦documents.openDocument⟧' }, { timeout: SlowEnvTimeout }));

      expect(screen.getByTestId('location').textContent).toBe(`/documents/11?periodKey=${String(Period)}`);
    },
    TestTimeout,
  );

  it(
    'Esc закриває шторку, прибирає ?panel= і вертає фокус на «око»',
    async () => {
      mockFetch();
      show();
      const user = userEvent.setup();

      const eye = await findEye();
      await user.click(eye);
      await screen.findByRole('dialog', {}, { timeout: SlowEnvTimeout });

      await user.keyboard('{Escape}');

      await waitFor(() => expect(screen.getByTestId('location').textContent).not.toContain('panel='), {
        timeout: SlowEnvTimeout,
      });
      await waitFor(() => expect(document.activeElement).toBe(eye), { timeout: SlowEnvTimeout });
    },
    TestTimeout,
  );
});

describe('DocumentQuickLook: чисті помічники', () => {
  it('sheetFill рахує лише таблиці аркуша, у яких людині є що вводити', () => {
    expect(sheetFill(tables, 'GEN')).toEqual({ filled: 2, total: 2 });
    expect(sheetFill(tables, 'AIR')).toEqual({ filled: 0, total: 1 });
    expect(sheetFill(tables, 'NONE')).toEqual({ filled: 0, total: 0 });
  });

  it('latestEvents — новіші першими, не більше трьох', () => {
    expect(latestEvents(history).map((item) => item.at)).toEqual([
      '2026-10-04T09:00:00Z',
      '2026-10-03T12:00:00Z',
      '2026-10-03T09:00:00Z',
    ]);
  });
});
