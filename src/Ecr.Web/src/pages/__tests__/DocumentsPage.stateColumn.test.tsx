import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { DocumentsPage } from '@/pages/DocumentsPage';
import { testTheme } from '@/test/render';

/**
 * UI-walkthrough F6 — «колонка State порожня без пояснення».
 *
 * На `/?periodKey=190001` (період, якого немає в календарі) рядок документа
 * показано, а клітинка стану — порожня. Нічого не відрізняє «за цей період
 * станів немає» від «не завантажилося»; порожнеча в таблиці читається двояко.
 *
 * ⚠ Тест дивиться саме на КЛІТИНКУ рядка, а не на сторінку загалом: «десь на
 * екрані є тире» — не те твердження, яке доводить знахідку.
 */
const MissingPeriod = 190001;

const project = { id: 1, code: 'AUDIT_SMOKE_PRJ', status: 'Active' as const };

const withoutStates = {
  id: 1,
  businessKey: 'DOC-000001',
  createdAt: '2026-01-01T00:00:00Z',
  projectId: 1,
  sheetCount: 1,
  sheetStates: {},
};

const withStates = {
  id: 2,
  businessKey: 'DOC-000002',
  createdAt: '2026-01-01T00:00:00Z',
  projectId: 1,
  sheetCount: 1,
  sheetStates: { S1: 'Draft' },
};

function mockFetch(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('/api/v1/me')) {
        return new Response(
          JSON.stringify({
            denies: [],
            grants: {},
            isSimulation: false,
            language: 'en',
            mustChangePassword: false,
            permissions: [],
            simulatedForUserId: null,
            userId: 1,
            userName: 'tester',
          }),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        );
      }

      if (url.includes('/api/v1/documents')) {
        return new Response(
          JSON.stringify({ items: [withoutStates, withStates], nextCursor: null, totalCount: 2 }),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        );
      }

      if (url.includes('/api/v1/projects')) {
        return new Response(JSON.stringify({ items: [project], nextCursor: null, totalCount: 1 }), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }

      return new Response(JSON.stringify(null), { status: 200 });
    }),
  );
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter initialEntries={[`/?periodKey=${String(MissingPeriod)}`]}>
        <QueryClientProvider client={client}>
          <DocumentsPage />
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

/** Клітинка «State» рядка з таким бізнес-ключем. */
function stateCellOf(businessKey: string): HTMLElement {
  const row = screen.getByText(businessKey).closest('tr');
  if (row === null) throw new Error(`Рядок ${businessKey} не знайдено`);

  const cells = within(row).getAllByRole('cell');

  return cells[3] as HTMLElement;
}

afterEach(() => {
  vi.unstubAllGlobals();
});

const SlowEnvTimeout = 400_000;

describe('DocumentsPage: відсутність станів позначена видимо (F6)', () => {
  it(
    'документ без станів за обраний період показує позначку відсутності, а не порожнечу',
    async () => {
      mockFetch();
      show();

      await screen.findByText('DOC-000001', {}, { timeout: SlowEnvTimeout });

      // ⛔ Мутаційний доказ: прибери гілку з «—» — і клітинка знову порожня,
      // обидва очікування падають.
      const empty = stateCellOf('DOC-000001');
      expect(empty.textContent?.trim()).not.toBe('');
      expect(empty.textContent?.trim()).toBe('—');
    },
    SlowEnvTimeout,
  );

  it(
    'документ зі станами показує бадж, а не позначку відсутності',
    async () => {
      mockFetch();
      show();

      await screen.findByText('DOC-000002', {}, { timeout: SlowEnvTimeout });

      const filled = stateCellOf('DOC-000002');
      expect(filled.textContent).toContain('S1: Draft');
      expect(filled.textContent).not.toContain('—');
    },
    SlowEnvTimeout,
  );
});
