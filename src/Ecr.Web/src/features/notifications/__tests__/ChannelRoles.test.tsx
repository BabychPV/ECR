import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { ChannelsPanel } from '@/features/notifications/ChannelsPanel';
import { testTheme } from '@/test/render';

/**
 * Ролі-адресати поштового каналу (`D-256`).
 *
 * ⛔ PUT каналу замінює ролі ЦІЛКОМ: форма, що їх не повертає, стирала б адресатів при кожній правці
 * назви. Тому навіть БЕЗ права на перелік ролей (вибору немає) збереження шле вже задані ролі.
 */

const channel = {
  id: 5,
  name: 'By role',
  kind: 'Smtp',
  isEnabled: true,
  hasSecret: false,
  modifiedAt: '2026-10-01T10:00:00Z',
  settings: { recipients: [], recipientRoleIds: [3, 4] },
  transportFromConfiguration: true,
  transportConfigured: true,
};

function json(body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  });
}

function mockServer(permissions: string[]): { puts: unknown[] } {
  const puts: unknown[] = [];

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const path = String(input).split('?')[0] ?? '';

      if (path.endsWith('/api/v1/notifications/channels/5') && init?.method === 'PUT') {
        puts.push(JSON.parse(String(init.body)));

        return json(channel);
      }

      if (path.endsWith('/api/v1/notifications/channels')) return json([channel]);
      if (path.endsWith('/api/v1/me')) return json({ permissions });
      if (path.endsWith('/api/v1/roles')) {
        return json([
          { id: 3, code: 'ECOLOGIST' },
          { id: 4, code: 'AUDITOR' },
        ]);
      }

      return json(null);
    }),
  );

  return { puts };
}

function show(): void {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <ChannelsPanel />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('ChannelsPanel: ролі-адресати', () => {
  it('без права на перелік ролей вибору немає, але збереження каналу НЕ стирає задані ролі', async () => {
    const server = mockServer([]);
    show();

    fireEvent.click(await screen.findByText(/notifications\.editChannel/));
    await screen.findByText(/notifications\.channelRolesUnavailable/);
    fireEvent.click(screen.getByText(/common\.save/));

    await waitFor(() => expect(server.puts).toHaveLength(1));
    const settings = (server.puts[0] as { settings: { recipientRoleIds: number[] } }).settings;
    expect(settings.recipientRoleIds).toEqual([3, 4]);
  }, 30_000);

  it('з правом ManageRoles ролі показані вибором, код ролі — підписом', async () => {
    mockServer(['Security.ManageRoles']);
    show();

    fireEvent.click(await screen.findByText(/notifications\.editChannel/));
    await screen.findByLabelText(/notifications\.channelRoles⟧/);

    await waitFor(() => expect(document.body.textContent ?? '').toContain('ECOLOGIST'));
    expect(document.body.textContent ?? '').toContain('AUDITOR');
  }, 30_000);
});
