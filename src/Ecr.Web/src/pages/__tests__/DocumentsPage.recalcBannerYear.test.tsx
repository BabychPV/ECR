import { afterEach, describe, expect, it, vi } from 'vitest';
import { configure, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { DocumentsPage } from '@/pages/DocumentsPage';
import { testTheme } from '@/test/render';

/**
 * Приймання 08.11, g2: «Finished: N recalculated…» перерахунку ІНШОГО проєкту стояв над переліком року, до якого той
 * не має стосунку. Без обраного проєкту банер читає останні власні перерахунки будь-якого проєкту (`projectId=null`).
 *
 * ⛔ Правило: на переліку документів — лише перерахунки проєктів, у яких є період ОБРАНОГО року (за обраним
 * проєктом — лише його). Мутаційний доказ: прибрати `onlyProjects` у `DocumentsPage.tsx` — перший випадок червоний.
 */

configure({ asyncUtilTimeout: 30_000 });

const project = { id: 1, code: 'PRJ-2026', status: 'Active' as const };

const calendar = {
  periods: [{ periodKey: 202610, year: 2026, sequence: 10, state: 'Open', isCurrent: true }],
};

const finishedRecalc = {
  jobId: 'RC-1',
  jobCode: 'Ecr.Application.Ports.IRecalculationJob',
  state: 'Succeeded',
  effectiveState: 'Succeeded',
  percent: 100,
  updatedAt: new Date().toISOString(),
  startedAt: new Date().toISOString(),
  documentId: null,
  projectId: 1,
  projectCode: 'PRJ-2026',
  fanOut: { total: 3, queued: 0, running: 0, succeeded: 3, failed: 0, skippedSheets: 0 },
};

let requested: string[] = [];

function serve(): void {
  requested = [];
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      requested.push(url);
      const json = (body: unknown): Response =>
        new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });

      if (url.includes('/api/v1/me')) {
        return json({ denies: [], grants: {}, isSimulation: false, language: 'en', mustChangePassword: false, permissions: [], simulatedForUserId: null, userId: 1, userName: 'tester' });
      }
      if (url.includes('/api/v1/jobs')) return json([finishedRecalc]);
      if (url.includes('/api/v1/documents/summary')) {
        return json({ draft: 0, submitted: 0, approved: 0, rejected: 0, withIssues: 0, partlySubmitted: 0 });
      }
      if (url.includes('/api/v1/documents/template-versions')) return json({ items: [], withoutVersionCount: 0 });
      if (url.includes('/api/v1/documents')) return json({ items: [], nextCursor: null, totalCount: 0 });
      if (/\/api\/v1\/projects\/1\/periods/.test(url)) return json(calendar);
      if (url.includes('/api/v1/projects')) return json({ items: [project], nextCursor: null, totalCount: 1 });

      return json(null);
    }),
  );
}

function show(path: string): void {
  render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter initialEntries={[path]}>
        <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
          <Routes>
            <Route path="/" element={<DocumentsPage />} />
          </Routes>
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
  globalThis.localStorage?.clear();
});

const Slow = 300_000;

describe('DocumentsPage: банер перерахунку — лише проєкти обраного року (g2)', () => {
  it(
    '2024: перерахунок проєкту 2026 не показується',
    async () => {
      serve();
      show('/?year=2024');

      expect(await screen.findByText('⟦documents.emptyYear (year=2024)⟧')).toBeTruthy();
      // Дані, з яких банер вирішує, уже прочитано: перелік перерахунків і календар проєкту.
      await waitFor(() => expect(requested.some((url) => url.includes('/api/v1/jobs'))).toBe(true));
      await waitFor(() => expect(requested.some((url) => /\/projects\/1\/periods/.test(url))).toBe(true));
      // Лінивий чанк банера мав би час доїхати.
      await new Promise((resolve) => setTimeout(resolve, 1_500));

      expect(document.querySelector('[data-recalc-banner]')).toBeNull();
    },
    Slow,
  );

  it(
    '2026: перерахунок проєкту цього року — на місці',
    async () => {
      serve();
      show('/?year=2026');

      await waitFor(() => expect(document.querySelector('[data-recalc-banner="RC-1"]')).not.toBeNull());
      expect(document.querySelector('[data-recalc-banner="RC-1"]')?.getAttribute('data-recalc-phase')).toBe('finished');
    },
    Slow,
  );
});
