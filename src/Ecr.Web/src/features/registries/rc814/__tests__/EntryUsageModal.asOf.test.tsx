import { afterEach, describe, expect, it, vi } from 'vitest';
import { configure, render, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ReactNode } from 'react';
import type { RegistryEntryDto } from '@/api/types';
import { testTheme } from '@/test/render';
import EntryUsageModal from '../EntryUsageModal';

/**
 * L9-42 (AUDIT-2026-10-03, 1E): дата звіту «Де використовується» фіксується на відкриття діалогу.
 *
 * До фіксу `todayIso()` (тепер `todayDateOnly`) викликався на кожен рендер: перший рендер після півночі давав новий ключ
 * запиту — звіт зникав за скелетом і перечитувався посеред читання.
 *
 * Мутаційний доказ (перевірено руками 2026-10-04): `const asOf = todayDateOnly();` замість `useState` →
 * червоний (другий запит і скелет після півночі).
 */
configure({ asyncUtilTimeout: 10_000 });

const Entry: RegistryEntryDto = {
  id: 42,
  code: 'NOX',
  display: 'Nitrogen oxides',
  parentEntryId: null,
  validFrom: null,
  validTo: null,
};

afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

describe('EntryUsageModal — дата звіту', () => {
  it('рендер після півночі не перечитує звіт і не ховає його за скелетом', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    vi.setSystemTime(new Date(2026, 9, 4, 23, 59, 50));

    const fetchMock = vi.fn(
      async () =>
        new Response(JSON.stringify({ items: [], total: 0 }), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        }),
    );
    vi.stubGlobal('fetch', fetchMock);

    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const wrap = (siblings: readonly RegistryEntryDto[]): ReactNode => (
      <MantineProvider theme={testTheme}>
        <QueryClientProvider client={client}>
          <MemoryRouter>
            <EntryUsageModal registryCode="SUBST" entry={Entry} siblings={siblings} onClose={() => {}} />
          </MemoryRouter>
        </QueryClientProvider>
      </MantineProvider>
    );

    const view = render(wrap([]));
    await waitFor(() => expect(document.querySelector('[data-entry-usage="report"]')).not.toBeNull());
    const reads = fetchMock.mock.calls.length;

    vi.setSystemTime(new Date(2026, 9, 5, 0, 0, 10));
    // Будь-який рендер батька (новий масив сусідів) — як оновлення переліку записів.
    view.rerender(wrap([]));

    expect(document.querySelector('[data-entry-usage="pending"]')).toBeNull();
    expect(document.querySelector('[data-entry-usage="report"]')).not.toBeNull();
    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(fetchMock.mock.calls.length).toBe(reads);
  });
});
