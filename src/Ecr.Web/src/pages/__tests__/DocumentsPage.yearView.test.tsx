import { afterEach, describe, expect, it, vi } from 'vitest';
import { configure, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { DocumentsPage } from '@/pages/DocumentsPage';
import { testTheme } from '@/test/render';

/**
 * ✎ Period clarity (людина 07.11, п. e): перелік документів — РІК. Рядок несе смугу 12 місяців (`periodStates` +
 * календар проєкту); кожна клітинка — посилання з текстом для читалки («March 2026: Submitted»), у порядку табуляції;
 * клік відкриває документ на цьому місяці.
 *
 * ✎ Projects/Documents 08.11 (макет `projects-documents-1108.html`): поля «Month» більше немає — перелік ЛИШЕ за рік;
 * `?periodKey=` зі старих посилань дає тільки рік. Календарі проєктів тепер запитуються (стан неподаного місяця й
 * варіанти «Year»), пояснення — `CollapsibleNote`, під фільтрами — легенда смуги.
 */

configure({ asyncUtilTimeout: 30_000 });

const project = { id: 1, code: 'PRJ-1', status: 'Active' as const };

const calendar = {
  periods: [
    { periodKey: 202601, year: 2026, sequence: 1, state: 'Closed', isCurrent: false },
    { periodKey: 202605, year: 2026, sequence: 5, state: 'Open', isCurrent: true },
    { periodKey: 202606, year: 2026, sequence: 6, state: 'Scheduled', isCurrent: false },
  ],
};

const row = {
  id: 42,
  businessKey: 'DOC-42',
  createdAt: '2026-01-01T00:00:00Z',
  projectId: 1,
  sheetCount: 1,
  sheetStates: {},
  hasLateEdits: false,
  periodStates: [
    { periodKey: 202601, state: 'Approved' },
    { periodKey: 202602, state: 'PartlySubmitted' },
    { periodKey: 202603, state: 'Submitted' },
    { periodKey: 202604, state: 'Rejected' },
    { periodKey: 202605, state: 'Draft' },
  ],
};

let requested: string[] = [];

/** `emptyYears` — роки, за які перелік порожній. */
function serve(emptyYears: readonly string[] = []): void {
  requested = [];
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      requested.push(url);
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
      if (url.includes('/api/v1/documents/summary')) {
        return json({ draft: 1, submitted: 0, approved: 0, rejected: 0, withIssues: 0, partlySubmitted: 0 });
      }
      if (url.includes('/api/v1/documents/template-versions')) return json({ items: [], withoutVersionCount: 0 });
      if (url.includes('/api/v1/documents')) {
        const year = new URL(url, 'http://x').searchParams.get('year') ?? '';
        const items = emptyYears.includes(year) ? [] : [row];
        return json({ items, nextCursor: null, totalCount: items.length });
      }
      if (/\/api\/v1\/projects\/1\/periods/.test(url)) return json(calendar);
      if (url.includes('/api/v1/projects')) return json({ items: [project], nextCursor: null, totalCount: 1 });

      return json(null);
    }),
  );
}

let search = '';
let pathname = '';
function LocationSpy(): null {
  const location = useLocation();
  search = location.search;
  pathname = location.pathname;
  return null;
}

function show(path: string): void {
  render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter initialEntries={[path]}>
        <LocationSpy />
        <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
          <Routes>
            <Route path="/" element={<DocumentsPage />} />
            <Route path="/documents/:id" element={<div data-testid="document-page" />} />
          </Routes>
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

const listRequests = (): URL[] =>
  requested.filter((url) => /\/api\/v1\/documents\?/.test(url)).map((url) => new URL(url, 'http://x'));

afterEach(() => {
  vi.unstubAllGlobals();
  search = '';
  pathname = '';
});

const Slow = 300_000;

describe('DocumentsPage: перегляд року зі смугою 12 місяців (Period clarity п. e)', () => {
  it(
    // ✎ Projects/Documents 08.11: календарі тепер ПОТРІБНІ (стан місяця, роки) — твердження «календарів ніхто не питає» замінено на зворотне; поля «Month» немає; пояснення — CollapsibleNote; є легенда.
    'рік: запит із year, без periodKey; календар проєкту запитано; поля місяця немає; пояснення й легенда видно',
    async () => {
      serve();
      show('/?year=2026');

      await screen.findByRole('list', { name: '⟦documents.yearStripLabel (key=DOC-42, year=2026)⟧' });

      expect(listRequests().some((u) => u.searchParams.get('year') === '2026')).toBe(true);
      expect(listRequests().every((u) => !u.searchParams.has('periodKey'))).toBe(true);
      expect(requested.some((url) => /\/projects\/1\/periods/.test(url))).toBe(true);
      expect(new URLSearchParams(search).has('periodKey')).toBe(false);

      const note = document.querySelector('[data-note="documents-year"]');
      expect(note?.textContent).toContain('⟦documents.oneDocumentNote⟧');
      expect(screen.queryByLabelText('⟦documents.month⟧')).toBeNull();
      expect(document.querySelector('[data-documents-month]')).toBeNull();
      expect(document.querySelector('[data-year-strip-legend]')).not.toBeNull();
      // Колонок місяця (аркуші, стан) немає.
      expect(screen.queryByRole('columnheader', { name: '⟦documents.state⟧' })).toBeNull();
      expect(screen.queryByRole('columnheader', { name: '⟦documents.sheets⟧' })).toBeNull();
      expect(document.querySelector('[data-documents-table]')?.getAttribute('data-documents-scope')).toBe('year');
    },
    Slow,
  );

  it(
    // ✎ Projects/Documents 08.11: підпис клітинки — «Місяць рік: стан смуги» (`documents.strip.*`), атрибут — `data-cell`; неподаний місяць бере стан періоду з календаря.
    'смуга — 12 посилань; стан кожного місяця — текстом (aria-label), не лише кольором; неподаний — стан періоду',
    async () => {
      serve();
      show('/?year=2026');

      const strip = await screen.findByRole('list', { name: /documents\.yearStripLabel/ });
      const links = within(strip).getAllByRole('link');
      expect(links).toHaveLength(12);

      // Календар приїжджає окремо: чекаємо, доки неподаний місяць набуде стану періоду.
      await waitFor(() => expect(links[5]?.getAttribute('data-cell')).toBe('notOpen'));

      const label = (month: string, state: string): string =>
        `⟦documents.monthState (month=${month} 2026, state=⟦documents.strip.${state}⟧)⟧`;
      expect(links[0]?.getAttribute('aria-label')).toBe(label('January', 'approved'));
      expect(links[1]?.getAttribute('aria-label')).toBe(label('February', 'submitted'));
      expect(links[2]?.getAttribute('aria-label')).toBe(label('March', 'submitted'));
      expect(links[3]?.getAttribute('aria-label')).toBe(label('April', 'returned'));
      // Чернетка у відкритому місяці — «open», і це поточний місяць.
      expect(links[4]?.getAttribute('aria-label')).toBe(label('May', 'open'));
      expect(links[4]?.getAttribute('data-current')).toBe('true');
      expect(links[5]?.getAttribute('aria-label')).toBe(label('June', 'notOpen'));
      // Місяця немає в календарі — «unknown», а не вигаданий стан.
      expect(links[11]?.getAttribute('aria-label')).toBe(label('December', 'unknown'));

      // Не лише колір: знак стану різний для «approved» і «open».
      expect(links[0]?.textContent).not.toBe(links[4]?.textContent);
      expect(links[0]?.getAttribute('data-cell')).toBe('approved');
      expect(links[11]?.getAttribute('data-cell')).toBe('unknown');

      // Справжні посилання — у порядку табуляції.
      links[2]?.focus();
      expect(document.activeElement).toBe(links[2]);
    },
    Slow,
  );

  it(
    'клік по місяцю відкриває документ саме на ньому',
    async () => {
      serve();
      show('/?year=2026');

      const strip = await screen.findByRole('list', { name: /documents\.yearStripLabel/ });
      const march = within(strip).getByRole('link', { name: /month=March 2026/ });
      expect(march.getAttribute('href')).toBe('/documents/42?periodKey=202603');

      fireEvent.click(march);
      await screen.findByTestId('document-page');
      expect(pathname).toBe('/documents/42');
      expect(search).toBe('?periodKey=202603');
    },
    Slow,
  );

  it(
    // ✎ Projects/Documents 08.11: перегляду за місяць більше немає — стара адреса з periodKey відкриває РІК цього місяця (смуга, запит з year).
    'стара адреса з periodKey — рік цього місяця: смуга на місці, запит з year без periodKey',
    async () => {
      serve();
      show('/?periodKey=202603');

      await screen.findByRole('list', { name: '⟦documents.yearStripLabel (key=DOC-42, year=2026)⟧' });
      expect(screen.queryByRole('columnheader', { name: '⟦documents.state⟧' })).toBeNull();
      expect((screen.getByLabelText('⟦documents.year⟧') as HTMLSelectElement).value).toBe('2026');

      await waitFor(() => expect(listRequests().some((u) => u.searchParams.get('year') === '2026')).toBe(true));
      expect(listRequests().every((u) => !u.searchParams.has('periodKey'))).toBe(true);
    },
    Slow,
  );

  it(
    'порожній рік: заголовок і підказка про рік, кнопка веде до найновішого року з проєктами',
    async () => {
      serve(['2024']);
      show('/?year=2024');

      expect(await screen.findByText('⟦documents.emptyYear (year=2024)⟧')).toBeTruthy();
      // Проєкт 2024 року не має — підказка без переліку проєктів.
      expect(screen.getByText('⟦documents.emptyYearNoProject (year=2024)⟧')).toBeTruthy();

      const action = await screen.findByRole('button', { name: '⟦documents.showYear (year=2026)⟧' });
      fireEvent.click(action);

      await waitFor(() => expect(new URLSearchParams(search).get('year')).toBe('2026'));
      await screen.findByRole('list', { name: '⟦documents.yearStripLabel (key=DOC-42, year=2026)⟧' });
    },
    Slow,
  );
});
