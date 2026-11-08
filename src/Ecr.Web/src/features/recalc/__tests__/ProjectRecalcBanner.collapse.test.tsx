import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, cleanup } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { ProjectRecalcBanner, bannerStorageKey } from '@/features/recalc/ProjectRecalcBanner';
import { testTheme } from '@/test/render';

/**
 * ✎ fg6 (людина 08.11): інформаційна панель компактна і згортається; згорнутий стан пам'ятається для КОРИСТУВАЧА
 * (localStorage) і показує підсумок одним рядком. Мутація: не писати `collapsed` у сховище — червоніє другий рендер.
 */
function serve(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async () =>
      new Response(
        JSON.stringify([
          {
            jobId: 'P1',
            jobCode: 'Ecr.Application.Ports.IRecalculationJob',
            state: 'Succeeded',
            percent: 100,
            updatedAt: new Date().toISOString(),
            startedAt: new Date().toISOString(),
            documentId: null,
            projectId: 7,
            projectCode: 'LANDUI2026',
            effectiveState: 'FannedOut',
            fanOut: { total: 92, queued: 80, running: 4, succeeded: 8, failed: 0, skippedSheets: 1 },
          },
        ]),
        { status: 200, headers: { 'Content-Type': 'application/json' } },
      ),
    ),
  );
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  client.setQueryData(['me'], { userId: 42, permissions: [], grants: {}, denies: [] });
  render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter>
        <QueryClientProvider client={client}>
          <ProjectRecalcBanner projectId={7} />
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
  globalThis.localStorage?.clear();
});

describe('ProjectRecalcBanner: згортання пам\'ятається для користувача (fg6)', () => {
  it('згорнутий — один рядок підсумку без смуги; після перезавантаження лишається згорнутим', async () => {
    serve();
    show();

    const text = '⟦recalcProgress.running (project=LANDUI2026, done=8, total=92, skipped=1, failed=0)⟧';
    expect(await screen.findByText(text)).toBeTruthy();
    expect(screen.getByRole('progressbar')).toBeTruthy();

    await userEvent.setup().click(screen.getByRole('button', { name: '⟦recalcProgress.collapse⟧' }));

    expect(screen.queryByRole('progressbar')).toBeNull();
    expect(screen.getByText(text)).toBeTruthy();
    expect(globalThis.localStorage.getItem(bannerStorageKey(42, 'collapsed'))).toBe('1');

    cleanup();
    show();

    expect(await screen.findByText(text)).toBeTruthy();
    expect(screen.queryByRole('progressbar')).toBeNull();
    expect(screen.getByRole('button', { name: '⟦recalcProgress.expand⟧' })).toBeTruthy();
  });
});
