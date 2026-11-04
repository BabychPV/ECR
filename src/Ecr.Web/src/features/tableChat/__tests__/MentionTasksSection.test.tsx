import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { generatePath, MemoryRouter } from 'react-router-dom';
import { routes } from '@/app/routes';
import { Catalog } from '@/test/__tests__/a11yFixtures';
import { testTheme } from '@/test/render';
import { MentionTasksSection, mentionTaskHref } from '../MentionTasksSection';

vi.mock('@/shared/i18n', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/i18n')>()),
  t: (key: string, params?: Record<string, string | number>) =>
    (Catalog[key] ?? `⟦${key}⟧`).replace(/\{(\w+)\}/g, (match, name: string) =>
      params !== undefined && name in params ? String(params[name]) : match,
    ),
}));

const Task = {
  messageId: 31,
  documentId: 42,
  tableCode: 'T3',
  periodKey: 202606,
  authorUserId: 3,
  authorName: 'Aigerim Sadykova',
  snippet: 'Please check <script>alert(1)</script> the fuel rate',
  createdAt: '2026-06-01T10:00:00Z',
};

function mount(response: () => Response, opened = true): void {
  vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(response())));
  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <MemoryRouter>
          <MentionTasksSection opened={opened} />
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('mention address', () => {
  it('leads to the document route and carries period, table and the open panel', () => {
    const href = mentionTaskHref(Task);
    const [path, query] = href.split('?');

    expect(path).toBe(generatePath(routes.documentDetail.path, { id: '42' }));
    expect(new URLSearchParams(query)).toEqual(new URLSearchParams({ periodKey: '202606', table: 'T3', panel: 'discussion' }));
  });
});

describe('the mentions block of My tasks', () => {
  it('lists the unread mentions: author, table, period, the snippet as text, and a link to the discussion', async () => {
    mount(() => new Response(JSON.stringify({ items: [Task] }), { status: 200, headers: { 'Content-Type': 'application/json' } }));

    const block = await screen.findByTestId('my-tasks-mentions');
    expect(block.textContent).toContain('Aigerim Sadykova · T3 ·');
    expect(block.querySelector('[data-mention-snippet]')?.textContent).toBe(Task.snippet);
    expect(block.querySelector('script')).toBeNull();
    expect(screen.getByRole('link', { name: Catalog['tableChat.taskOpen'] ?? '' }).getAttribute('href')).toBe(mentionTaskHref(Task));
  });

  it('is silent when there is nothing, and does not ask while the drawer is closed', async () => {
    mount(() => new Response(JSON.stringify({ items: [] }), { status: 200, headers: { 'Content-Type': 'application/json' } }));

    await waitFor(() => expect(globalThis.fetch).toHaveBeenCalled());
    expect(screen.queryByTestId('my-tasks-mentions')).toBeNull();
    cleanup();
    vi.unstubAllGlobals();

    mount(() => new Response('{}', { status: 200 }), false);
    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(globalThis.fetch).not.toHaveBeenCalled();
  });

  it('a server without the route (404) is an empty block, a real failure is visible with a retry', async () => {
    mount(() => new Response('{}', { status: 404, headers: { 'Content-Type': 'application/problem+json' } }));
    await waitFor(() => expect(globalThis.fetch).toHaveBeenCalled());
    expect(screen.queryByTestId('my-tasks-mentions-error')).toBeNull();
    cleanup();
    vi.unstubAllGlobals();

    mount(() => new Response('{}', { status: 500, headers: { 'Content-Type': 'application/problem+json' } }));
    expect(await screen.findByTestId('my-tasks-mentions-error')).toBeTruthy();
    expect(screen.getByRole('button', { name: Catalog['common.retry'] ?? '' })).toBeTruthy();
  });
});
