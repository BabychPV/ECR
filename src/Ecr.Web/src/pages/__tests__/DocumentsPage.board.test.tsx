import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter, useLocation } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { JSX } from 'react';
import { DocumentsPage } from '@/pages/DocumentsPage';
import { testTheme } from '@/test/render';

/**
 * `UI-40`: подання «Board» (макет `screens-work.js` → `paintBoard`, `12-docs-board.png`).
 *
 * Приймання: 1) стовпці за станом, число в заголовку; 2) картка відкриває документ / quick look;
 * 3) фільтри спільні з таблицею (вони в адресі, перемикач їх не скидає).
 * Плюс P1: `errorCount: null` → «—», а не «0»; число аркушів — лише видимі.
 */
const Period = 202609;
const SlowEnvTimeout = 20_000;
const TestTimeout = 60_000;

const doc = (id: number, states: Record<string, string>, extra: Record<string, unknown> = {}) => ({
  id,
  businessKey: `DOC-0000${String(id)}`,
  nameL10n: { values: { en: `Facility ${String(id)}` } },
  createdAt: '2026-01-01T00:00:00Z',
  projectId: 1,
  sheetCount: Object.keys(states).length,
  sheetStates: states,
  sheets: Object.entries(states).map(([code, state]) => ({ code, nameL10n: { values: { en: code } }, state })),
  modifiedAt: '2026-10-05T11:42:00Z',
  modifiedByDisplayName: 'Aigerim Sadykova',
  errorCount: 0,
  warningCount: 0,
  hasLateEdits: false,
  ...extra,
});

const documents = [
  doc(11, { GEN: 'Draft', AIR: 'Approved' }, { errorCount: 3 }),
  doc(12, { GEN: 'Submitted' }, { hasLateEdits: true }),
  // ⛔ P1: роль бачить ОДИН аркуш (`sheetCount` сервер уже рахує за видимими); помилок не знає.
  doc(13, { GEN: 'Rejected' }, { errorCount: null, warningCount: null }),
  doc(14, { GEN: 'Approved', AIR: 'Approved' }),
];
let items: unknown[] = documents;
let summary: unknown = { draft: 1, submitted: 1, approved: 1, rejected: 1, withIssues: 1 };
let calendar: unknown = null;

const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
  const url = String(input);
  const json = (body: unknown): Response =>
    new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });

  if (url.includes('/api/v1/me')) {
    return json({ denies: [], grants: {}, isSimulation: false, language: 'en', mustChangePassword: false, permissions: [], simulatedForUserId: null, userId: 1, userName: 'tester' });
  }
  if (url.includes('/tables/status')) return json([]);
  if (url.includes('/workflow/history')) return json([]);
  if (url.includes('/api/v1/documents/summary')) return json(summary);
  if (url.includes('/api/v1/documents')) return json({ items, nextCursor: null, totalCount: items.length });
  if (url.includes('/periods') && calendar !== null) return json(calendar);
  if (url.includes('/api/v1/projects')) return json({ items: [{ id: 1, code: 'ATR', status: 'Active' }], nextCursor: null, totalCount: 1 });

  return json(null);
});

function LocationProbe(): JSX.Element {
  const location = useLocation();

  return <span data-testid="location">{location.pathname + location.search}</span>;
}

function show(url: string): void {
  vi.stubGlobal('fetch', fetchMock);
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

const board = async (): Promise<HTMLElement> =>
  waitFor(() => {
    const node = document.querySelector<HTMLElement>('[data-documents-board]');
    expect(node).not.toBeNull();

    return node as HTMLElement;
  }, { timeout: SlowEnvTimeout });

const column = (root: HTMLElement, id: string): HTMLElement =>
  root.querySelector<HTMLElement>(`[data-board-column="${id}"]`) as HTMLElement;

afterEach(() => {
  vi.unstubAllGlobals();
  fetchMock.mockClear();
  items = documents;
  summary = { draft: 1, submitted: 1, approved: 1, rejected: 1, withIssues: 1 };
  calendar = null;
});

describe('DocumentsPage — подання Board (UI-40)', () => {
  it(
    'стовпці за станом, число в заголовку, кожен стовпець — список',
    async () => {
      show(`/?periodKey=${String(Period)}&view=board`);
      const root = await board();

      // ✎ b14: «частково подано» — власний стовпець (DOC-000011: чернетка поруч із затвердженим).
      expect([...root.querySelectorAll('[data-board-column]')].map((node) => node.getAttribute('data-board-column'))).toEqual([
        'Draft',
        'PartlySubmitted',
        'Submitted',
        'Rework',
        'Approved',
      ]);
      const cards = (id: string) => [...column(root, id).querySelectorAll('[data-board-card]')].map((node) => node.getAttribute('data-board-card'));
      expect(cards('Draft')).toEqual([]);
      expect(cards('PartlySubmitted')).toEqual(['DOC-000011']);
      expect(cards('Submitted')).toEqual(['DOC-000012']);
      expect(cards('Rework')).toEqual(['DOC-000013']);
      expect(cards('Approved')).toEqual(['DOC-000014']);

      // Стовпець — `<section>` із заголовком і списком (a11y: «список, N елементів»).
      expect(within(column(root, 'Submitted')).getByRole('heading', { name: /documents\.board\.waiting/ })).toBeTruthy();
      expect(within(column(root, 'Submitted')).getByRole('list')).toBeTruthy();
      expect(column(root, 'Rework').querySelector('[data-board-count]')?.getAttribute('data-board-count')).toBe('1');
      // Колір лише в стовпця, що чекає уваги.
      expect(column(root, 'Rework').querySelector('[data-board-count]')?.getAttribute('data-tone')).toBe('warn');
      expect(column(root, 'Draft').querySelector('[data-board-count]')?.getAttribute('data-tone')).toBeNull();
      // Таблиці в поданні «Board» немає.
      expect(document.querySelector('[data-documents-table]')).toBeNull();
    },
    TestTimeout,
  );

  it(
    'картка: помилки «3», невідомі помилки — «—», пізні правки, бейдж лише у «Returned or rejected»',
    async () => {
      show(`/?periodKey=${String(Period)}&view=board`);
      const root = await board();
      const card = (key: string) => root.querySelector<HTMLElement>(`[data-board-card="${key}"]`) as HTMLElement;

      expect(card('DOC-000011').querySelector('[data-issue-count]')?.textContent).toBe('3');
      // ⛔ P1 / BE-09: `null` — не «0».
      expect(card('DOC-000013').querySelector('[data-issue-count]')).toBeNull();
      expect(within(card('DOC-000013')).getByText('—')).toBeTruthy();
      expect(card('DOC-000012').querySelector('[data-late-edits]')).not.toBeNull();
      expect(card('DOC-000013').textContent).toContain('status.sheet.Rejected');
      expect(card('DOC-000014').textContent).not.toContain('status.sheet.');
      // Смужка — рівно видимі аркуші.
      expect(card('DOC-000013').textContent).toMatch(/segments\.approvedOf \(done=0, total=1\)/);
    },
    TestTimeout,
  );

  it(
    'посилання картки веде на документ (без місяця), «око» відкриває швидкий перегляд',
    async () => {
      // ✎ Projects/Documents 08.11: перелік лише за рік — стара адреса з periodKey дає рік, а картка веде на документ без місяця (місяць обирають у документі).
      show(`/?periodKey=${String(Period)}&view=board`);
      const root = await board();

      expect(within(root).getByRole('link', { name: 'DOC-000012' }).getAttribute('href')).toBe('/documents/12');

      await userEvent.click(within(root).getByRole('button', { name: /documents\.quickLook \(key=DOC-000012\)/ }));
      await waitFor(() => expect(screen.getByTestId('location').textContent).toContain('panel=12'), { timeout: SlowEnvTimeout });
    },
    TestTimeout,
  );

  it(
    'клік по картці поза посиланням відкриває документ',
    async () => {
      show(`/?periodKey=${String(Period)}&view=board`);
      const root = await board();

      await userEvent.click(within(root.querySelector('[data-board-card="DOC-000014"]') as HTMLElement).getByText('Facility 14'));
      // ✎ Projects/Documents 08.11: перелік лише за рік — місяць у адресу документа більше не переноситься.
      await waitFor(() => expect(screen.getByTestId('location').textContent).toBe('/documents/14'), { timeout: SlowEnvTimeout });
    },
    TestTimeout,
  );

  it(
    'перемикач Table/Board — в адресі; фільтри при перемиканні лишаються',
    async () => {
      show(`/?periodKey=${String(Period)}&state=Draft&mine=true`);
      const radio = await screen.findByRole('radio', { name: /documents\.viewBoard/ }, { timeout: SlowEnvTimeout });

      await userEvent.click(radio);
      await board();
      const location = screen.getByTestId('location').textContent ?? '';
      expect(location).toContain('view=board');
      expect(location).toContain('state=Draft');
      expect(location).toContain('mine=true');

      await userEvent.click(screen.getByRole('radio', { name: /documents\.viewTable/ }));
      await waitFor(() => expect(document.querySelector('[data-documents-table]')).not.toBeNull(), { timeout: SlowEnvTimeout });
      expect(screen.getByTestId('location').textContent).not.toContain('view=');
    },
    TestTimeout,
  );

  it(
    'документ без станів за період не зникає — окремий стовпець «No state for this period»',
    async () => {
      items = [...documents, doc(15, {})];
      show(`/?periodKey=${String(Period)}&view=board`);
      const root = await board();

      expect(within(column(root, 'NoState')).getByRole('heading', { name: /documents\.board\.noState/ })).toBeTruthy();
      expect(column(root, 'NoState').querySelector('[data-board-card="DOC-000015"]')).not.toBeNull();
      expect(root.querySelectorAll('[data-board-card]').length).toBe(5);
    },
    TestTimeout,
  );

  /*
   * ⛔ AC-1 (b14 documents-board-yearly, людина 25.11): перелік за РІК — у рядках немає станів аркушів (`sheetStates`
   * порожній), є `periodStates`. Над дошкою «4 Draft · 1 Partly submitted», а всі 5 карток стояли в «No state for this
   * period». Дошка й лічильники мають казати одне: число в заголовку стовпця = число на смузі.
   * Без фіксу червоний: стовпці Draft/PartlySubmitted порожні, з'являється `NoState` з 5 картками.
   */
  it(
    'AC-1: рік — картки в тих самих стовпцях, що й лічильники; стовпця «без стану» немає',
    async () => {
      const suffix = String(Date.now() % 100000);
      const yearDoc = (id: number, states: readonly string[]) => ({
        ...doc(id, {}),
        businessKey: `Y-${suffix}-${String(id)}`,
        sheets: [],
        // Жовтень-грудень ще не відкрито (Scheduled): сервер віддає їх `Draft`, у рік вони не входять.
        periodStates: [...states, 'Draft', 'Draft', 'Draft'].map((state, index) => ({ periodKey: 202601 + index, state })),
      });
      const nine = (state: string) => Array.from({ length: 9 }, () => state);
      items = [
        yearDoc(21, nine('Draft')),
        yearDoc(22, nine('Draft')),
        yearDoc(23, nine('Draft')),
        yearDoc(24, nine('Draft')),
        // Січень–серпень подано, вересень перевідкрито й повернуто в чернетку.
        yearDoc(25, [...Array.from({ length: 8 }, () => 'Submitted'), 'Draft']),
      ];
      summary = { draft: 4, partlySubmitted: 1, submitted: 0, approved: 0, rejected: 0, withIssues: 0 };
      calendar = {
        timeZoneId: 'UTC',
        periods: Array.from({ length: 12 }, (_, index) => ({
          periodKey: 202601 + index,
          state: index < 9 ? (index === 8 ? 'Open' : 'Closed') : 'Scheduled',
          isCurrent: index === 8,
        })),
      };
      show('/?year=2026&view=board');
      const root = await board();

      const counter = async (id: string): Promise<string> =>
        waitFor(() => {
          const node = document.querySelector(`[data-summary-counter="${id}"]`);
          expect(node).not.toBeNull();
          return (node as HTMLElement).textContent ?? '';
        }, { timeout: SlowEnvTimeout });
      expect(await counter('draft')).toMatch(/^4/);
      expect(await counter('partlySubmitted')).toMatch(/^1/);

      const count = (id: string) => column(root, id).querySelector('[data-board-count]')?.getAttribute('data-board-count');
      await waitFor(() => expect(count('Draft')).toBe('4'), { timeout: SlowEnvTimeout });
      expect(count('PartlySubmitted')).toBe('1');
      expect(column(root, 'PartlySubmitted').querySelector(`[data-board-card="Y-${suffix}-25"]`)).not.toBeNull();
      expect(count('Submitted')).toBe('0');
      expect(root.querySelector('[data-board-column="NoState"]')).toBeNull();
    },
    TestTimeout,
  );
});