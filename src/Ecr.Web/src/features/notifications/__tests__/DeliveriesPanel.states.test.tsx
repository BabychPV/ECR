import { afterEach, describe, expect, it, vi } from 'vitest';
import { configure, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { DeliveriesPanel } from '@/features/notifications/DeliveriesPanel';
import { testTheme } from '@/test/render';

/**
 * Журнал доставок: стани «немає права» і «завантаження» (`ФВ-14.22`).
 *
 * ⚠ 500 і порожній журнал уже стереже `DeliveriesPanel.test.tsx`. Тут — 403 і
 * запит у дорозі: «сповіщень не було» — твердження про минуле, і ні відмова в
 * праві, ні незавершений запит його робити не мають.
 */
configure({ asyncUtilTimeout: 10_000 });

function serve(answer: () => Promise<Response>): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      if (!url.includes('/api/v1/notifications/deliveries')) throw new Error(`Немає мока для ${url}`);

      return answer();
    }),
  );
}

function show(): void {
  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <DeliveriesPanel />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

function noEmptyTexts(): void {
  expect(screen.queryByText('⟦notifications.noDeliveries⟧')).toBeNull();
  expect(screen.queryByText('⟦notifications.noDeliveriesHint⟧')).toBeNull();
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('DeliveriesPanel — стани', () => {
  it('403: відмова з кодом, а не «сповіщень не було»', async () => {
    serve(() =>
      Promise.resolve(
        new Response(
          JSON.stringify({ title: 'Forbidden', status: 403, errorCode: 'ECR-AUTH-0403', correlationId: 'c', detail: null }),
          { status: 403, headers: { 'Content-Type': 'application/problem+json' } },
        ),
      ),
    );
    show();

    expect((await screen.findByRole('alert')).textContent).toContain('ECR-AUTH-0403');
    noEmptyTexts();
    expect(screen.queryByRole('table')).toBeNull();
  });

  it('у дорозі: «завантаження», а не «сповіщень не було»', async () => {
    serve(() => new Promise<Response>(() => undefined));
    show();

    expect((await screen.findByRole('status')).getAttribute('aria-busy')).toBe('true');
    noEmptyTexts();
    expect(screen.queryByRole('table')).toBeNull();
    expect(screen.queryByRole('alert')).toBeNull();
  });
});
