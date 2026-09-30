import type { JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { TableRelationsPage } from '@/pages/admin/TableRelationsPage';
import { ChannelsPanel } from '@/features/notifications/ChannelsPanel';
import { testTheme } from '@/test/render';

/**
 * R-06/X-01 (четвертий раунд UX): зв'язок таблиць і канал сповіщень
 * видалялися одним натисканням, без питання; канал — ще й без жодного
 * відгуку, поки запит летів.
 */

function json(body: unknown, status = 200): Response {
  return new Response(body === null ? null : JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

function serve(): string[] {
  const deletes: string[] = [];

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const path = String(input).split('?')[0] ?? '';

      if (init?.method === 'DELETE') {
        deletes.push(path);
        return json(null, 204);
      }
      if (path.endsWith('/api/v1/me')) {
        return json({
          denies: [],
          grants: {},
          isSimulation: false,
          language: 'en',
          mustChangePassword: false,
          permissions: ['Template.Edit', 'Template.View', 'System.ManageNotifications'],
          simulatedForUserId: null,
          userId: 1,
          userName: 'tester',
        });
      }
      if (path.endsWith('/relations')) {
        return json({
          isEditable: true,
          relations: [
            {
              id: 1,
              code: 'FEED',
              relationKind: 'Aggregation',
              sourceTableDefId: 500,
              sourceTableCode: 'T1',
              targetTableDefId: 501,
              targetTableCode: 'T2',
              onSourceChange: 'Recalculate',
              isActive: true,
            },
          ],
        });
      }
      if (path.endsWith('/api/v1/notifications/channels')) {
        return json([
          {
            id: 4,
            name: 'Ops mailbox',
            kind: 'Smtp',
            isEnabled: true,
            hasSecret: true,
            modifiedAt: '2026-09-01T10:00:00Z',
            settings: { recipients: ['ops@example.org'] },
            transportFromConfiguration: true,
          },
        ]);
      }

      return json(null);
    }),
  );

  return deletes;
}

function withProviders(node: JSX.Element): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>{node}</QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('Видалення лише після підтвердження (R-06/X-01)', () => {
  it('зв\'язок таблиць: питання з кодом, DELETE — лише після підтвердження', async () => {
    const deletes = serve();
    withProviders(
      <MemoryRouter initialEntries={['/admin/templates/1/versions/5/relations']}>
        <Routes>
          <Route path="/admin/templates/:id/versions/:versionId/relations" element={<TableRelationsPage />} />
        </Routes>
      </MemoryRouter>,
    );

    fireEvent.click(await screen.findByRole('button', { name: '⟦tables.deleteRelation⟧' }));
    const dialog = await screen.findByRole('dialog');

    expect(within(dialog).getByText('⟦tables.deleteRelationTitle (code=FEED)⟧')).toBeDefined();
    expect(deletes).toHaveLength(0);

    fireEvent.click(within(dialog).getByTestId('confirm-verb'));
    await waitFor(() => expect(deletes).toEqual(['/api/v1/template-versions/5/relations/FEED']));
  });

  it('канал сповіщень: питання з назвою, DELETE — лише після підтвердження', async () => {
    const deletes = serve();
    withProviders(<ChannelsPanel />);

    await screen.findByText('Ops mailbox');
    fireEvent.click(screen.getByRole('button', { name: '⟦common.delete⟧' }));
    const dialog = await screen.findByRole('dialog');

    expect(within(dialog).getByText('⟦notifications.deleteChannelTitle (name=Ops mailbox)⟧')).toBeDefined();
    expect(deletes).toHaveLength(0);

    fireEvent.click(within(dialog).getByTestId('confirm-verb'));
    await waitFor(() => expect(deletes).toEqual(['/api/v1/notifications/channels/4']));
  });
});
