import { describe, expect, it, vi, afterEach } from 'vitest';
import { render, screen, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { DocumentsPage } from '@/pages/DocumentsPage';
import { testTheme } from '@/test/render';

/**
 * ✎ B74 P4 (D-576, рішення людини 13.11): у проєкті «рік · об'єкт» документи різних шаблонів — перелік показує
 * шаблон окремою колонкою «Template» (`templateCode` рядка). Документ без шаблону (`null`, до ремонту
 * 99-epilogue) — видиме «—», а не порожнеча.
 */
const project = { id: 1, code: 'P2026-SITE', status: 'Active' as const };

const base = { createdAt: '2026-01-01T00:00:00Z', projectId: 1, sheetCount: 1, sheetStates: {} };
const documents = [
  { ...base, id: 1, businessKey: 'P1-0001', templateCode: 'GEN' },
  { ...base, id: 2, businessKey: 'LAND-P1-0001', templateCode: 'LAND' },
  { ...base, id: 3, businessKey: 'P1-0002', templateCode: null },
];

function json(body: unknown): Response {
  return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
}

function mockFetch(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('/api/v1/me')) {
        return json({
          denies: [], grants: {}, isSimulation: false, language: 'en', mustChangePassword: false, permissions: [],
          simulatedForUserId: null, userId: 1, userName: 'tester',
        });
      }

      if (url.includes('/api/v1/documents')) return json({ items: documents, nextCursor: null, totalCount: documents.length });

      if (url.includes('/api/v1/projects')) return json({ items: [project], nextCursor: null, totalCount: 1 });

      return json(null);
    }),
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

const SlowEnvTimeout = 400_000;

/** Клітинка колонки «Template» рядка з таким бізнес-ключем. */
function templateCell(businessKey: string): HTMLElement {
  const row = document.querySelector(`[data-document-row="${businessKey}"]`);
  const cell = row?.querySelector('[data-column="template"]');
  if (!(cell instanceof HTMLElement)) throw new Error(`Колонку template у рядку ${businessKey} не знайдено`);

  return cell;
}

describe('DocumentsPage: колонка «Template» (B74 P4)', () => {
  it(
    'заголовок «Template», у рядку — код шаблону документа; без шаблону — «—»',
    async () => {
      mockFetch();
      render(
        <MantineProvider theme={testTheme}>
          <MemoryRouter initialEntries={['/']}>
            <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
              <DocumentsPage />
            </QueryClientProvider>
          </MemoryRouter>
        </MantineProvider>,
      );

      const table = await screen.findByRole('table', {}, { timeout: SlowEnvTimeout });
      await within(table).findByText('LAND-P1-0001', {}, { timeout: SlowEnvTimeout });

      expect(within(table).getByRole('columnheader', { name: '⟦documents.template⟧' })).toBeTruthy();
      expect(templateCell('P1-0001').textContent).toBe('GEN');
      expect(templateCell('LAND-P1-0001').textContent).toBe('LAND');
      expect(templateCell('P1-0002').textContent).toBe('—');
    },
    SlowEnvTimeout,
  );
});
