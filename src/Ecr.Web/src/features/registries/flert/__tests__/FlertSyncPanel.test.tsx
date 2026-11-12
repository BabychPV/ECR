import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { FlertSyncPanel } from '../FlertSyncPanel';
import { testTheme } from '@/test/render';

/**
 * Стан синку FLERT на сторінці довідника-дзеркала і «Синхронізувати зараз» (D-476 F3).
 *
 * ⚠ Каталог рядків не завантажений — підписи приходять ключами в `⟦…⟧`.
 */

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

const Run = {
  startedAt: '2026-11-12T02:00:00Z',
  finishedAt: '2026-11-12T02:00:03Z',
  status: 'Degraded',
  reason: 'emptyResult',
  sourceRows: 0,
  added: 0,
  updated: 0,
  deactivated: 0,
  reactivated: 0,
  issueCount: 0,
  message: null,
};

interface Server {
  readonly posts: string[];
  readonly statusReads: () => number;
}

function mockServer(permissions: string[], lastRun: unknown = Run, enabled = true): Server {
  const posts: string[] = [];
  let statusReads = 0;
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const path = String(input).split('?')[0] ?? '';
      if (path.endsWith('/api/v1/me')) {
        return json({
          denies: [],
          grants: {},
          isSimulation: false,
          language: 'en',
          mustChangePassword: false,
          permissions,
          simulatedForUserId: null,
          userId: 9,
          userName: 'tester',
        });
      }

      if (path.endsWith('/api/v1/integration/flert-sync/status')) {
        statusReads++;
        return json({ enabled, registryCodes: ['STREAM', 'STREAM_CASE', 'GAS_COMPOSITION'], lastRun });
      }

      if (path.endsWith('/api/v1/integration/flert-sync') && init?.method === 'POST') {
        posts.push(path);
        return json({ jobId: 'IFlertCompositionSyncJob-1' }, 202);
      }

      return json({ title: 'not found' }, 404);
    }),
  );
  return { posts, statusReads: () => statusReads };
}

function renderPanel(code: string): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <FlertSyncPanel registryCode={code} />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('FlertSyncPanel', () => {
  it('на довіднику-дзеркалі показує останній прогін, лічильники й причину, кнопку — з Integration.Manage', async () => {
    const server = mockServer(['Integration.Manage']);
    renderPanel('stream_case');

    const panel = await screen.findByTestId('flert-sync');
    expect(panel.textContent).toContain('⟦flert.sync.title⟧');
    expect(panel.textContent).toContain('⟦flert.sync.lastRun');
    expect(panel.textContent).toContain('⟦flert.sync.counts');
    expect(panel.textContent).toContain('⟦flert.sync.reason.emptyResult⟧');
    expect(panel.textContent).not.toContain('⟦flert.sync.disabled⟧');

    fireEvent.click(screen.getByRole('button', { name: '⟦flert.sync.now⟧' }));
    await waitFor(() => expect(server.posts).toEqual(['/api/v1/integration/flert-sync']));
    await waitFor(() => expect(server.statusReads()).toBeGreaterThan(1));
  });

  it('лише з Integration.View — стан видно, кнопки немає', async () => {
    mockServer(['Integration.View']);
    renderPanel('STREAM');

    await screen.findByTestId('flert-sync');
    expect(screen.queryByRole('button', { name: '⟦flert.sync.now⟧' })).toBeNull();
  });

  it('прогонів ще не було і синк вимкнено — так і каже', async () => {
    mockServer(['Integration.Manage'], null, false);
    renderPanel('GAS_COMPOSITION');

    const panel = await screen.findByTestId('flert-sync');
    expect(panel.textContent).toContain('⟦flert.sync.never⟧');
    expect(panel.textContent).toContain('⟦flert.sync.disabled⟧');
  });

  it('на іншому довіднику нічого не малює', async () => {
    const server = mockServer(['Integration.Manage']);
    renderPanel('COMPONENT');

    await waitFor(() => expect(server.statusReads()).toBe(1));
    expect(screen.queryByTestId('flert-sync')).toBeNull();
  });

  it('без прав інтеграції стан навіть не читається', async () => {
    const server = mockServer(['Registry.EditData']);
    renderPanel('STREAM');

    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(server.statusReads()).toBe(0);
    expect(screen.queryByTestId('flert-sync')).toBeNull();
  });
});
