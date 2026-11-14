import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { HealthPage, diagnostics } from '@/pages/admin/HealthPage';

vi.mock('@/shared/ui/notify', () => ({ showDone: vi.fn(), showApiError: vi.fn() }));

const Facts = {
  productVersion: '1.4.2',
  startedAt: '2026-09-19T03:00:00Z',
  environment: 'Production',
  notificationTransport: { isConfigured: false, kind: null },
  logDirectory: null,
};

function calculations(enabled: boolean, warning: boolean, source = 'Environment'): object {
  return {
    ...Facts,
    calculations: {
      hse400CompositionFromDocument: enabled,
      hse400CompositionSource: source,
      hse400CompositionWarning: warning,
    },
  };
}

function serve(facts: unknown): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const body = String(input).includes('/api/v1/health/facts')
        ? facts
        : { status: 'Healthy', totalDurationMs: 1, checks: [] };

      return Promise.resolve(
        new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } }),
      );
    }),
  );
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider>
      <QueryClientProvider client={client}>
        <HealthPage />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('B105: прапор складу HSE400 на сторінці «Здоров’я системи»', () => {
  it('увімкнений прапор показується значенням і джерелом, без попередження', async () => {
    serve(calculations(true, false, 'ProgramData'));
    show();

    expect(await screen.findByText('⟦health.facts.hse400Composition⟧')).toBeDefined();
    expect(screen.getByText('⟦health.facts.hse400Composition.on⟧ · ProgramData')).toBeDefined();
    expect(document.querySelector('[data-testid="health-hse400-warning"]')).toBeNull();
  });

  it('вимкнений прапор за наявного документа HSE400 поточного року дає попередження', async () => {
    serve(calculations(false, true, 'AppSettings'));
    show();

    expect(await screen.findByText('⟦health.hse400CompositionOff.title⟧')).toBeDefined();
    expect(screen.getByText('⟦health.hse400CompositionOff.text⟧')).toBeDefined();
    expect(screen.getByText('⟦health.facts.hse400Composition.off⟧ · AppSettings')).toBeDefined();
  });

  it('вимкнений прапор без документа поточного року — лише значення, без попередження', async () => {
    serve(calculations(false, false, 'Default'));
    show();

    expect(await screen.findByText('⟦health.facts.hse400Composition.off⟧ · Default')).toBeDefined();
    expect(screen.queryByText('⟦health.hse400CompositionOff.title⟧')).toBeNull();
  });

  it('старий процес без блоку calculations — ні рядка, ні банера', async () => {
    serve(Facts);
    show();

    await screen.findByText('1.4.2');
    expect(screen.queryByText('⟦health.facts.hse400Composition⟧')).toBeNull();
    expect(screen.queryByText('⟦health.hse400CompositionOff.title⟧')).toBeNull();
  });

  it('діагностика для тикета називає прапор, джерело й попередження', () => {
    const text = diagnostics({
      ready: undefined,
      db: undefined,
      facts: calculations(false, true, 'Environment') as Parameters<typeof diagnostics>[0]['facts'],
      checkedAt: null,
    });

    expect(text).toContain('Hse400CompositionFromDocument: false (Environment) · WARNING');
  });
});
