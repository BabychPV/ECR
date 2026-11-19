import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { HealthPage, diagnostics } from '@/pages/admin/HealthPage';

vi.mock('@/shared/ui/notify', () => ({ showDone: vi.fn(), showApiError: vi.fn() }));

/**
 * ✎ B80 N6 (клієнт до N1): ворота зв'язки HSE400 → Land на сторінці «Здоров'я системи» — стан (`any` /
 * `approvedOnly`), затверджені місяці й попередження `hse400SourceWarning` з текстом за станом.
 */
const Facts = {
  productVersion: '1.4.2',
  startedAt: '2026-09-19T03:00:00Z',
  environment: 'Production',
  notificationTransport: { isConfigured: false, kind: null },
  logDirectory: null,
};

function facts(source: { state: string | null; months: number | null; warning: boolean }): object {
  return {
    ...Facts,
    calculations: {
      hse400CompositionFromDocument: true,
      hse400CompositionSource: 'ProgramData',
      hse400CompositionWarning: false,
      hse400SourceState: source.state,
      hse400ApprovedMonths: source.months,
      hse400SourceWarning: source.warning,
    },
  };
}

function serve(body: unknown): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const payload = String(input).includes('/api/v1/health/facts')
        ? body
        : { status: 'Healthy', totalDurationMs: 1, checks: [] };

      return Promise.resolve(
        new Response(JSON.stringify(payload), { status: 200, headers: { 'Content-Type': 'application/json' } }),
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

const banner = (): Element | null => document.querySelector('[data-testid="health-hse400-source-warning"]');

/** Текст значення рядка фактів «Land: HSE400 source gate» (сусід мітки в парі ключ-значення). */
function sourceValue(): string {
  const label = screen.getByText('⟦health.facts.hse400Source⟧');
  const pair = label.parentElement;

  return (pair?.textContent ?? '').replace(label.textContent ?? '', '');
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('B80 N6: ворота джерела HSE400 → Land на сторінці «Здоров’я системи»', () => {
  it('approvedOnly, усі місяці з даними затверджені — рядок фактів зі станом і місяцями, без попередження', async () => {
    serve(facts({ state: 'approvedOnly', months: 11, warning: false }));
    show();

    expect(await screen.findByText('⟦health.facts.hse400Source⟧')).toBeDefined();
    const value = sourceValue();
    expect(value).toContain('⟦health.facts.hse400Source.approvedOnly⟧ · ');
    expect(value).toContain('health.facts.hse400Source.approvedMonths');
    expect(banner()).toBeNull();
  });

  it('any — попередження з текстом «ще читає незатверджене»', async () => {
    serve(facts({ state: 'any', months: 3, warning: true }));
    show();

    expect(await screen.findByText('⟦health.hse400SourceWarning.title⟧')).toBeDefined();
    expect(screen.getByText('⟦health.hse400SourceWarning.textAny⟧')).toBeDefined();
    expect(screen.queryByText('⟦health.hse400SourceWarning.textApprovedOnly⟧')).toBeNull();
    const value = sourceValue();
    expect(value).toContain('⟦health.facts.hse400Source.any⟧ · ');
    expect(value).toContain('health.facts.hse400Source.approvedMonths');
  });

  it('approvedOnly, але місяць із даними не затверджений — попередження з текстом «Land без складу»', async () => {
    serve(facts({ state: 'approvedOnly', months: 10, warning: true }));
    show();

    expect(await screen.findByText('⟦health.hse400SourceWarning.textApprovedOnly⟧')).toBeDefined();
    expect(screen.queryByText('⟦health.hse400SourceWarning.textAny⟧')).toBeNull();
  });

  it('зв’язки немає (state null) — ні рядка, ні попередження (D15-06)', async () => {
    serve(facts({ state: null, months: null, warning: false }));
    show();

    expect(await screen.findByText('⟦health.facts.hse400Composition⟧')).toBeDefined();
    expect(screen.queryByText('⟦health.facts.hse400Source⟧')).toBeNull();
    expect(banner()).toBeNull();
  });

  it('невідомий стан воріт — мітка сервера, без вигаданого перекладу; місяців немає — без них', async () => {
    serve(facts({ state: 'block', months: null, warning: false }));
    show();

    expect(await screen.findByText('⟦health.facts.hse400Source⟧')).toBeDefined();
    expect(screen.getByText('block')).toBeDefined();
  });

  it('копія діагностики містить стан воріт, місяці й попередження', () => {
    const text = diagnostics({
      ready: undefined,
      db: undefined,
      facts: facts({ state: 'any', months: 3, warning: true }) as never,
      checkedAt: null,
    } as never);

    expect(text).toContain('Hse400SourceState: any · approved months: 3 · WARNING: unapproved source');
  });
});
