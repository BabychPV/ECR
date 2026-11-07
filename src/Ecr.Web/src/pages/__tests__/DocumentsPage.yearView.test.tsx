import { afterEach, describe, expect, it, vi } from 'vitest';
import { configure, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { DocumentsPage } from '@/pages/DocumentsPage';
import { testTheme } from '@/test/render';

/**
 * ✎ Period clarity (людина 07.11, п. e): перелік документів типово — РІК. Рядок несе смугу 12 місяців
 * (`periodStates`); кожна клітинка — посилання з текстом для читалки («March: Submitted»), у порядку табуляції;
 * клік відкриває документ на цьому місяці. Автовибору місяця при вході немає (календарі не запитуються).
 */

configure({ asyncUtilTimeout: 30_000 });

const project = { id: 1, code: 'PRJ-1', status: 'Active' as const };

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

function serve(): void {
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
      if (url.includes('/api/v1/documents')) return json({ items: [row], nextCursor: null, totalCount: 1 });
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

afterEach(() => {
  vi.unstubAllGlobals();
  search = '';
  pathname = '';
});

const Slow = 300_000;

describe('DocumentsPage: перегляд року зі смугою 12 місяців (Period clarity п. e)', () => {
  it(
    'типово — рік: запит із year, без periodKey і без автовибору місяця; пояснення видно',
    async () => {
      serve();
      show('/?year=2026');

      await screen.findByRole('list', { name: '⟦documents.yearStripLabel (key=DOC-42, year=2026)⟧' });

      const list = requested.filter((url) => /\/api\/v1\/documents\?/.test(url)).map((url) => new URL(url, 'http://x'));
      expect(list.some((u) => u.searchParams.get('year') === '2026' && !u.searchParams.has('periodKey'))).toBe(true);
      // ⛔ Автовибір місяця прибрано: календарів ніхто не питає, адреса без periodKey.
      expect(requested.some((url) => /\/projects\/\d+\/periods/.test(url))).toBe(false);
      expect(new URLSearchParams(search).has('periodKey')).toBe(false);
      expect(screen.getByText('⟦documents.oneDocumentNote⟧')).toBeTruthy();
      expect((screen.getByLabelText('⟦documents.month⟧') as HTMLSelectElement).value).toBe('');
      // Колонки місяця (аркуші, стан) у перегляді року не малюються.
      expect(screen.queryByRole('columnheader', { name: '⟦documents.state⟧' })).toBeNull();
    },
    Slow,
  );

  it(
    'смуга — 12 посилань; стан кожного місяця — текстом (aria-label), не лише кольором; місяць без стану — «no data»',
    async () => {
      serve();
      show('/?year=2026');

      const strip = await screen.findByRole('list', { name: /documents\.yearStripLabel/ });
      const links = within(strip).getAllByRole('link');
      expect(links).toHaveLength(12);

      expect(links[0]?.getAttribute('aria-label')).toBe('⟦documents.monthState (month=January, state=⟦status.sheet.Approved⟧)⟧');
      expect(links[1]?.getAttribute('aria-label')).toBe(
        '⟦documents.monthState (month=February, state=⟦documents.statePartlySubmitted⟧)⟧',
      );
      expect(links[2]?.getAttribute('aria-label')).toBe('⟦documents.monthState (month=March, state=⟦status.sheet.Submitted⟧)⟧');
      expect(links[3]?.getAttribute('aria-label')).toBe('⟦documents.monthState (month=April, state=⟦status.sheet.Rejected⟧)⟧');
      expect(links[11]?.getAttribute('aria-label')).toBe('⟦documents.monthNoState (month=December)⟧');

      // Не лише колір: знак стану різний для Draft і Approved (той самий тон `neutral`).
      expect(links[0]?.textContent).not.toBe(links[4]?.textContent);
      expect(links[0]?.getAttribute('data-month-state')).toBe('Approved');
      expect(links[11]?.getAttribute('data-month-state')).toBe('none');

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
      const march = within(strip).getByRole('link', { name: /month=March/ });
      expect(march.getAttribute('href')).toBe('/documents/42?periodKey=202603');

      fireEvent.click(march);
      await screen.findByTestId('document-page');
      expect(pathname).toBe('/documents/42');
      expect(search).toBe('?periodKey=202603');
    },
    Slow,
  );

  it(
    'обраний місяць — колишній перегляд за місяць (аркуші й стан), запит з periodKey; рік у смузі зникає',
    async () => {
      serve();
      show('/?periodKey=202603');

      await screen.findByRole('columnheader', { name: '⟦documents.state⟧' });
      expect(screen.queryByRole('list', { name: /documents\.yearStripLabel/ })).toBeNull();
      expect((screen.getByLabelText('⟦documents.month⟧') as HTMLSelectElement).value).toBe('3');
      expect((screen.getByLabelText('⟦documents.year⟧') as HTMLSelectElement).value).toBe('2026');

      await waitFor(() =>
        expect(
          requested.some((url) => /\/api\/v1\/documents\?/.test(url) && url.includes('periodKey=202603') && !url.includes('year=')),
        ).toBe(true),
      );
    },
    Slow,
  );
});
