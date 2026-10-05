import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import type { RegistryDefDto } from '@/api/types';
import { loadCatalog } from '@/shared/i18n';
import { testTheme } from '@/test/render';
import { EntryDrawer } from '../EntryDrawer';
import { mockServer, storedRows } from './fixtures';

/**
 * D-212 (UI): нетемпоральний довідник не має календарної чинності — у шторці запису немає
 * ні полів «Valid from/to», ні дії «Validity». Темпоральний — має.
 *
 * ⛔ Мутаційний доказ: ігнорувати `temporal` у EntryDrawer (завжди true) → перший тест червоний.
 */
function show(isTemporal: boolean): void {
  const registry = { id: 1, code: 'STREAM_CASE', nameL10n: { values: { en: 'x' } }, isTemporal } as unknown as RegistryDefDto;
  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <MemoryRouter initialEntries={['/x?panel=entry-4411']}>
          <EntryDrawer registry={registry} row={{ ...storedRows[0]!, validFrom: '2026-01-01', validTo: '2026-12-31' }} fields={[]} asOf={isTemporal ? '2026-10-02' : null} readOnly={false} />
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

beforeEach(async () => {
  mockServer();
  await loadCatalog('en', 'private');
  await loadCatalog('en', 'public');
});
afterEach(() => vi.unstubAllGlobals());

describe('EntryDrawer: чинність лише для темпорального довідника (D-212)', () => {
  it('нетемпоральний: полів дат і дії «Validity» немає', async () => {
    show(false);
    await screen.findByRole('tablist');
    expect(screen.queryByText('Valid from')).toBeNull();
    expect(screen.queryByText('Valid to')).toBeNull();
    expect(screen.queryByRole('button', { name: 'Valid' })).toBeNull();
    expect(screen.getByRole('button', { name: /Edit/ })).toBeDefined();
  });

  it('«станом на» шукає запис за id, а не підрядком коду — короткий код не губиться за лімітом (L9-08)', async () => {
    // ⚠ Сервер на `q` віддає 50 ІНШИХ рядків (підрядок коду в чужих кодах/назвах, сортування за Id),
    // і лише точний фільтр `id` повертає шуканий.
    const base = globalThis.fetch;
    const queries: string[] = [];
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
        const url = new URL(String(input), 'http://localhost');
        if (url.pathname.endsWith('/history')) {
          return new Response(JSON.stringify({ items: [], nextCursor: null, totalCount: 0 }), {
            status: 200,
            headers: { 'Content-Type': 'application/json' },
          });
        }
        if (url.pathname.endsWith('/registries/STREAM_CASE/rows') && url.searchParams.has('asOfUtc')) {
          queries.push(url.search);
          const others = Array.from({ length: 50 }, (_, i) => ({ ...storedRows[1]!, id: 100 + i, code: `X${String(i)}` }));
          const items = url.searchParams.getAll('id').includes('4411') ? [storedRows[0]] : others;
          return new Response(JSON.stringify({ items, nextCursor: null, totalCount: items.length }), {
            status: 200,
            headers: { 'Content-Type': 'application/json' },
          });
        }
        return base(input, init);
      }),
    );
    show(false);

    fireEvent.click(await screen.findByRole('tab', { name: 'History' }));
    const input = await screen.findByRole('textbox', { name: /Values as of/ });
    fireEvent.change(input, { target: { value: '2026-09-27' } });
    fireEvent.blur(input);

    expect(await screen.findByRole('table', { name: 'Values as of' })).toBeDefined();
    expect(screen.queryByText(/did not exist/)).toBeNull();
    expect(queries.every((query) => query.includes('id=4411') && !query.includes('q='))).toBe(true);
  });

  it('темпоральний: поля дат і дія «Validity» є', async () => {
    show(true);
    await screen.findByRole('tablist');
    expect(screen.getByText('Valid from')).toBeDefined();
    expect(screen.getByText('Valid to')).toBeDefined();
    expect(screen.getByRole('button', { name: 'Valid' })).toBeDefined();
  });
});
