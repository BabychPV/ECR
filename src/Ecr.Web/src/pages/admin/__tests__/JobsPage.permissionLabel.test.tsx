import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { loadCatalog } from '@/shared/i18n';
import { JobsPage } from '@/pages/admin/JobsPage';

/**
 * Приймання 08.11, a5: підказка під «Only my jobs» показувала сирий код права «System.ViewHealth». Тепер — та сама
 * людська назва, що й на екрані відмови й у матриці прав (`permissionLabel`, ключ `permission.System.ViewHealth`).
 *
 * ⛔ Мутаційний доказ: `t('jobs.mineOnlyHint')` без параметра `permission` — на екрані «{permission}», тест червоний.
 */
const Strings: Record<string, string> = {
  'jobs.mineOnly': 'Only my jobs',
  'jobs.mineOnlyHint': 'Your own jobs are visible without the «{permission}» permission; the full queue is not.',
  'permission.System.ViewHealth': 'View system health and the job queue',
  'jobs.recentEmpty': 'No jobs yet',
};

function mockFetch(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      const json = (body: unknown): Response =>
        new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });

      if (url.includes('/ui-strings/')) return json({ languageCode: 'en', revision: 1, strings: Strings });
      if (url.includes('/api/v1/jobs')) return json([]);
      return json(null);
    }),
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('JobsPage: назва права в підказці (a5)', () => {
  it('підказка «Only my jobs» називає право людською назвою, без коду', async () => {
    mockFetch();
    await loadCatalog('en', 'private');

    render(
      <MantineProvider>
        <MemoryRouter initialEntries={['/admin/jobs']}>
          <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
            <JobsPage />
          </QueryClientProvider>
        </MemoryRouter>
      </MantineProvider>,
    );

    const hint = await screen.findByText(
      'Your own jobs are visible without the «View system health and the job queue» permission; the full queue is not.',
    );
    expect(hint.textContent).not.toContain('System.ViewHealth');
    expect(hint.textContent).not.toContain('{permission}');
  });
});
