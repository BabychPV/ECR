import { afterEach, describe, expect, it, vi } from 'vitest';
import { configure, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { RegistryEntryDto } from '@/api/types';
import { testTheme } from '@/test/render';
import EntryUsageModal from '../EntryUsageModal';

/**
 * Діалог «Де використовується» запису (ФВ-8.14): стани «помилка 500»,
 * «порожньо» (крізь мережу) і «завантаження» (`ФВ-14.22`).
 *
 * ⚠ 403 і подання порожнього звіту без мережі вже покриті в
 * `EntryUsageModal.test.tsx` — тут не дублюються.
 */
configure({ asyncUtilTimeout: 10_000 });

const NoneNamed = '[data-entry-usage="none-named"]';

const Entry: RegistryEntryDto = {
  id: 42,
  code: 'NOX',
  display: 'Nitrogen oxides',
  parentEntryId: null,
  validFrom: null,
  validTo: null,
};

function serve(usage: () => Promise<Response>): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async () => usage()),
  );
}

function show(): void {
  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <MemoryRouter>
          <EntryUsageModal registryCode="SUBST" entry={Entry} siblings={[]} onClose={() => {}} />
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('EntryUsageModal — стани', () => {
  it('500: помилка з кодом, а не «поіменних посилань немає»', async () => {
    serve(() =>
      Promise.resolve(
        new Response(
          JSON.stringify({ title: 'Server error', status: 500, errorCode: 'ECR-SYS-0500', correlationId: 'c', detail: null }),
          { status: 500, headers: { 'Content-Type': 'application/problem+json' } },
        ),
      ),
    );
    show();

    expect((await screen.findByRole('alert')).textContent).toContain('ECR-SYS-0500');
    expect(document.querySelector(NoneNamed)).toBeNull();
    expect(document.querySelector('[data-entry-usage="report"]')).toBeNull();
  });

  it('порожньо: звіт із «поіменних посилань немає», без помилки', async () => {
    serve(() =>
      Promise.resolve(
        new Response(JSON.stringify({ items: [], total: 0 }), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        }),
      ),
    );
    show();

    await waitFor(() => expect(document.querySelector(NoneNamed)).not.toBeNull());
    expect(screen.queryByRole('alert')).toBeNull();
    expect(document.querySelector('[data-entry-usage="pending"]')).toBeNull();
  });

  it('у дорозі: скелет, а не порожній звіт', async () => {
    serve(() => new Promise<Response>(() => undefined));
    show();

    await waitFor(() => expect(document.querySelector('[data-entry-usage="pending"]')).not.toBeNull());
    expect(document.querySelector(NoneNamed)).toBeNull();
    expect(document.querySelector('[data-entry-usage="report"]')).toBeNull();
  });
});
