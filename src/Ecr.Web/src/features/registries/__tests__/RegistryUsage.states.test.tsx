import { afterEach, describe, expect, it, vi } from 'vitest';
import { configure, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { testTheme } from '@/test/render';
import { RegistryUsagePanel } from '../RegistryUsage';

/**
 * Вкладка «Де використано» довідника: стани «завантаження» і «немає права»
 * (`ФВ-14.22`).
 *
 * ⚠ «Порожньо» і 500 уже покриті в `RegistryUsage.test.tsx` — тут не
 * дублюються.
 */
configure({ asyncUtilTimeout: 10_000 });

const None = '⟦registries.usageNone⟧';

function serve(usage: () => Promise<Response>): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      if (String(input).includes('/usage')) return usage();

      return new Response('null', { status: 200, headers: { 'Content-Type': 'application/json' } });
    }),
  );
}

function show(): HTMLElement {
  return render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter>
        <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
          <RegistryUsagePanel code="Flares" />
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  ).container;
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('RegistryUsagePanel — стани', () => {
  it('у дорозі: скелет, а не «ніде не використано»', () => {
    serve(() => new Promise<Response>(() => undefined));
    const container = show();

    expect(container.querySelector('[data-registry-usage="pending"]')).not.toBeNull();
    expect(screen.queryByText(None)).toBeNull();
    expect(container.querySelector('[data-registry-usage="none"]')).toBeNull();
  });

  it('403: відмова з кодом, а не «ніде не використано»', async () => {
    serve(() =>
      Promise.resolve(
        new Response(
          JSON.stringify({
            title: 'Forbidden',
            status: 403,
            errorCode: 'ECR-AUTH-0403',
            correlationId: 'corr-usage',
            detail: null,
          }),
          { status: 403, headers: { 'Content-Type': 'application/problem+json' } },
        ),
      ),
    );
    const container = show();

    expect((await screen.findByRole('alert')).textContent).toContain('ECR-AUTH-0403');
    expect(screen.queryByText(None)).toBeNull();
    expect(container.querySelector('[data-registry-usage="pending"]')).toBeNull();
  });
});
