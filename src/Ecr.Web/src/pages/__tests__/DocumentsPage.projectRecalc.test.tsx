import { describe, it, expect, vi, afterEach } from 'vitest';
import { configure, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { DocumentsPage } from '@/pages/DocumentsPage';
import { testTheme } from '@/test/render';

/**
 * ✎ Period clarity (людина 07.11, п. b): перерахунок УСЬОГО проєкту — дія переліку документів (переїхала зі
 * сторінки Periods). Права ті самі: `Calculation.Recalculate`, лише активному проєкту; підтвердження ПЕРЕД запитом.
 */

configure({ asyncUtilTimeout: 30_000 });

type Project = { id: number; code: string; status: 'Active' | 'Draft' };

function respond(projects: readonly Project[], permissions: readonly string[]): { posts: string[] } {
  const state = { posts: [] as string[] };

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const json = (body: unknown, code = 200): Response =>
        new Response(JSON.stringify(body), { status: code, headers: { 'Content-Type': 'application/json' } });

      if (init?.method === 'POST') {
        state.posts.push(url);
        return json({ jobId: null, nothingToRecalculate: true, skippedSheets: [] });
      }

      if (url.includes('/api/v1/me')) {
        return json({
          denies: [],
          grants: {},
          isSimulation: false,
          language: 'en',
          mustChangePassword: false,
          permissions,
          simulatedForUserId: null,
          userId: 1,
          userName: 'tester',
        });
      }

      if (url.includes('/api/v1/documents')) return json({ items: [], nextCursor: null, totalCount: 0 });
      if (url.includes('/api/v1/projects')) return json({ items: projects, nextCursor: null, totalCount: projects.length });

      return json(null);
    }),
  );

  return state;
}

function show(entry = '/'): void {
  render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter initialEntries={[entry]}>
        <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
          <DocumentsPage />
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

const Slow = 300_000;
const Prj = { id: 7, code: 'PRJ-7', status: 'Active' as const };
const Other = { id: 8, code: 'PRJ-8', status: 'Active' as const };

describe('DocumentsPage: перерахунок проєкту (Period clarity п. b)', () => {
  it(
    'обраний проєкт і право — підтвердження, запит лише після нього',
    async () => {
      const state = respond([Prj, Other], ['Calculation.Recalculate', 'Document.View']);
      const user = userEvent.setup();
      show('/?projectId=7');

      await user.click(await screen.findByRole('button', { name: '⟦workflow.recalculate⟧' }));
      const dialog = await screen.findByRole('dialog', { name: '⟦periods.recalcTitle (code=PRJ-7)⟧' });
      // ⛔ Мутація «`recalculate.mutate` прямо на кнопці» шле POST уже тут.
      expect(state.posts).toEqual([]);

      await user.click(within(dialog).getByRole('button', { name: '⟦workflow.recalculate⟧' }));
      await waitFor(() => expect(state.posts).toEqual(['/api/v1/projects/7/recalculate']));
    },
    Slow,
  );

  it(
    'без права — дії немає',
    async () => {
      respond([Prj], ['Document.View']);
      show('/?projectId=7');
      await screen.findByText(/⟦documents\.(empty|noMatch)⟧/);
      expect(screen.queryByRole('button', { name: '⟦workflow.recalculate⟧' })).toBeNull();
    },
    Slow,
  );

  it(
    'кілька проєктів без вибору — дії немає',
    async () => {
      respond([Prj, Other], ['Calculation.Recalculate']);
      show('/');
      await screen.findByText(/⟦documents\.(empty|noMatch)⟧/);
      expect(screen.queryByRole('button', { name: '⟦workflow.recalculate⟧' })).toBeNull();
    },
    Slow,
  );
});
