import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { HealthPage } from '@/pages/admin/HealthPage';

/**
 * N4-07: «Check now» — тост «перевірено» лише коли всі запити вдалися; відмова — тост помилки, а не «успіх»;
 * повторне натискання, поки перевірка триває, нових запитів не запускає.
 */
const shown = vi.hoisted(() => ({ done: vi.fn(), error: vi.fn() }));

vi.mock('@/shared/ui/notify', () => ({
  showDone: shown.done,
  showApiError: shown.error,
}));

const Facts = {
  productVersion: '1.4.2',
  startedAt: '2026-09-19T03:00:00Z',
  environment: 'Production',
  notificationTransport: { isConfigured: false, kind: null },
  logDirectory: null,
};

function ok(body: unknown): Response {
  return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
}

/** `failing` — чи віддає `/health/db` 500; `gate` — затримує відповіді, поки тест не відпустить. */
function serve(control: { failing: boolean; gate: Promise<void> | null }): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (control.gate !== null) await control.gate;

      if (url.includes('/health/db') && control.failing) {
        return new Response('{"title":"boom"}', { status: 500, headers: { 'Content-Type': 'application/json' } });
      }

      return ok(url.includes('/api/v1/health/facts') ? Facts : { status: 'Healthy', totalDurationMs: 1, checks: [] });
    }),
  );
}

function show(): void {
  render(
    <MantineProvider>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <HealthPage />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
  shown.done.mockClear();
  shown.error.mockClear();
});

describe('HealthPage: «Check now» (N4-07)', () => {
  it('усі запити вдалися — тост «перевірено»', async () => {
    serve({ failing: false, gate: null });
    show();

    fireEvent.click(await screen.findByRole('button', { name: '⟦health.checkNow⟧' }));

    await waitFor(() => expect(shown.done).toHaveBeenCalledWith('⟦health.checkedNow⟧'));
    expect(shown.error).not.toHaveBeenCalled();
  });

  it('один із запитів відмовив — немає тосту «перевірено», є тост помилки', async () => {
    const control = { failing: false, gate: null as Promise<void> | null };
    serve(control);
    show();

    const button = await screen.findByRole('button', { name: '⟦health.checkNow⟧' });
    await waitFor(() => expect(vi.mocked(fetch).mock.calls.length).toBeGreaterThanOrEqual(3));

    control.failing = true;
    fireEvent.click(button);

    await waitFor(() => expect(shown.error).toHaveBeenCalled());
    expect(shown.done).not.toHaveBeenCalledWith('⟦health.checkedNow⟧');
  });

  it('повторне натискання, поки перевірка триває, не запускає другу', async () => {
    const control = { failing: false, gate: null as Promise<void> | null };
    serve(control);
    show();

    const button = await screen.findByRole('button', { name: '⟦health.checkNow⟧' });
    await waitFor(() => expect(vi.mocked(fetch).mock.calls.length).toBeGreaterThanOrEqual(3));

    let release: () => void = () => undefined;
    control.gate = new Promise<void>((resolve) => {
      release = resolve;
    });
    const before = vi.mocked(fetch).mock.calls.length;

    fireEvent.click(button);
    fireEvent.click(button);
    fireEvent.click(button);
    release();

    await waitFor(() => expect(shown.done).toHaveBeenCalledTimes(1));
    // Один раунд = рівно три запити (ready, db, facts).
    expect(vi.mocked(fetch).mock.calls.length - before).toBe(3);
  });
});
