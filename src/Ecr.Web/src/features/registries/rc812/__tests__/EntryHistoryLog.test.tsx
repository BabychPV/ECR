import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import type { RegistryEntryHistoryItem, RegistryEntryHistoryPage } from '@/features/registries/rows/api';
import { loadCatalog } from '@/shared/i18n';
import { testTheme } from '@/test/render';
import { historyValueText } from '../entryHistory';
import { RegistryDataPage } from '../RegistryDataPage';
import { mockServer } from './fixtures';

/**
 * Журнал змін запису довідника (RT-15) у шторці запису: хто, коли, що, «було» → «стало», сторінками
 * за курсором; чанк і запит — лише коли відкрили вкладку «History».
 *
 * ⚠ Тексти — САМ `09-seed.sql` (через `fixtures.mockServer`): ключ, забутий у сіді, дав би `⟦…⟧`.
 *
 * ⚠ Мутаційні докази (перевірено руками, 2026-09-30; кожна мутація — червоний тест):
 *   - `historyValueText` бере `value` раніше за `display` → «рядок зміни Lookup…»;
 *   - без `?? t('…unknownAuthor')` → «автор невідомий…»;
 *   - `getNextPageParam` → `undefined` (курсор губиться) → «ранні зміни — наступною сторінкою…»;
 *   - без умови `tab === 'history'` у шторці → «журнал не вантажиться, поки вкладку не відкрили»;
 *   - гілку `validity` прибрано → «інтервал чинності…».
 */

const item = (over: Partial<RegistryEntryHistoryItem>): RegistryEntryHistoryItem => ({
  at: '2026-09-29T10:15:00Z',
  byDisplayName: 'Olena Koval',
  byUserId: 7,
  field: null,
  kind: 'value',
  newValue: null,
  oldValue: null,
  newDisplay: null,
  oldDisplay: null,
  ...over,
});

const firstPage: RegistryEntryHistoryPage = {
  items: [
    item({ field: 'STREAM', oldValue: '161', oldDisplay: '1D-1 · LP Gas', newValue: '162', newDisplay: '1D-2 · HP Separator Gas' }),
    item({ field: 'T_C', oldValue: '48.1', newValue: '49.9999977539011', byDisplayName: null, byUserId: null }),
  ],
  nextCursor: 'c2',
  totalCount: 3,
};

const secondPage: RegistryEntryHistoryPage = {
  items: [item({ kind: 'created', at: '2026-09-01T08:00:00Z' })],
  nextCursor: null,
  totalCount: 3,
};

let historyUrls: string[];

beforeEach(async () => {
  historyUrls = [];
  mockServer();
  const base = vi.mocked(globalThis.fetch);
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      if (url.includes('/entries/4411/history')) {
        historyUrls.push(url);
        const body = url.includes('cursor=c2') ? secondPage : firstPage;
        return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
      }
      return base(input, init);
    }),
  );
  await loadCatalog('en', 'private');
  await loadCatalog('en', 'public');
});

afterEach(() => {
  vi.unstubAllGlobals();
});

function showDrawer(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={['/admin/registries/STREAM_CASE/entries?panel=entry-4411']}>
          <Routes>
            <Route path="/admin/registries/:code/entries" element={<RegistryDataPage />} />
          </Routes>
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

async function openHistory(): Promise<HTMLElement> {
  fireEvent.click(await screen.findByRole('tab', { name: 'History' }));
  return screen.findByTestId('entry-history');
}

describe('Журнал змін запису довідника (RT-15)', () => {
  it('журнал не вантажиться, поки вкладку не відкрили', async () => {
    // Чанк журналу — уже в кеші модулів: якби шторка монтувала його одразу, запит пішов би за мить.
    await import('../EntryHistoryLog');
    showDrawer();
    await screen.findByRole('tab', { name: 'History' });
    await new Promise((resolve) => setTimeout(resolve, 500));

    expect(historyUrls).toEqual([]);

    await openHistory();
    expect(historyUrls).toEqual(['/api/v1/registries/STREAM_CASE/entries/4411/history?limit=50']);
  });

  it('рядок зміни Lookup: поле назвою, «було» і «стало» — назвами цілей, автор — ім\'ям', async () => {
    showDrawer();
    const table = await openHistory();

    const rows = within(table).getAllByRole('row');
    const lookup = rows[1];
    if (lookup === undefined) throw new Error('немає рядка');
    expect(within(lookup).getByText('Stream')).toBeDefined();
    expect(within(lookup).getByText('1D-1 · LP Gas')).toBeDefined();
    expect(within(lookup).getByText('1D-2 · HP Separator Gas')).toBeDefined();
    expect(within(lookup).getByText('Olena Koval')).toBeDefined();
    expect(within(lookup).queryByText('161')).toBeNull();
  });

  it('автор невідомий — словами; число — рядком без втрати знаків', async () => {
    showDrawer();
    const table = await openHistory();

    const row = within(table).getAllByRole('row')[2];
    if (row === undefined) throw new Error('немає рядка');
    expect(within(row).getByText('Unknown (background job)')).toBeDefined();
    expect(within(row).getByText('Temperature')).toBeDefined();
    expect(within(row).getByText('49.9999977539011')).toBeDefined();
  });

  it('ранні зміни — наступною сторінкою за курсором сервера', async () => {
    showDrawer();
    await openHistory();

    fireEvent.click(screen.getByRole('button', { name: 'Show earlier changes' }));

    expect(await screen.findByText('Entry created')).toBeDefined();
    expect(historyUrls[1]).toBe('/api/v1/registries/STREAM_CASE/entries/4411/history?cursor=c2&limit=50');
    await waitFor(() => {
      expect(screen.queryByRole('button', { name: 'Show earlier changes' })).toBeNull();
    });
  });

  it('інтервал чинності і прапорець активності — словами', () => {
    expect(historyValueText(item({ kind: 'validity', oldValue: '2026-01-01/..' }), 'old')).toBe('2026-01-01 — open');
    expect(historyValueText(item({ kind: 'validity', newValue: '2026-01-01/2027-01-01' }), 'new')).toBe('2026-01-01 — 2027-01-01');
    expect(historyValueText(item({ kind: 'active', newValue: 'false' }), 'new')).toBe('No');
    expect(historyValueText(item({ kind: 'value', oldValue: null }), 'old')).toBe('');
  });
});
