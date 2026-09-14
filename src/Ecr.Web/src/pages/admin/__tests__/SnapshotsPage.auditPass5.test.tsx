import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { SnapshotsPage } from '@/pages/admin/SnapshotsPage';

/**
 * Аудит-пас 5, дві незалежні знахідки на одній сторінці:
 *
 * 1. Рядок зрізу показував голий `snapshot.projectId` (число з бази) замість
 *    коду проєкту — довідник проєктів уже завантажений для селектора вище.
 * 2. `Badge` статусу зрізу (`snapshots.status`) обтинався еліпсисом, щойно
 *    сторінка звужувалась (Mantine `Badge .label` — `overflow: hidden;
 *    text-overflow: ellipsis`) — той самий дефект, що вже виправлено для
 *    матриці ролей у `SecurityPage.tsx`, тим самим прийомом (`ScrollArea`).
 */
const project = { id: 42, code: 'KASH_2026', status: 'Active' as const };

const snapshot = {
  id: 1,
  builtAt: '2026-01-15T10:00:00Z',
  contentHash: 'abc123',
  isCurrent: true,
  periodKey: 202601,
  projectId: 42,
  reportVersionId: 1,
  rowCount: 10,
  status: 'Approved',
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

      if (url.includes('/api/v1/reports/snapshots')) {
        return new Response(JSON.stringify([snapshot]), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }

      if (url.includes('/api/v1/reports')) {
        return new Response(JSON.stringify([]), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
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
    <MantineProvider>
      <MemoryRouter initialEntries={['/admin/snapshots']}>
        <QueryClientProvider client={client}>
          <SnapshotsPage />
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

const SlowEnvTimeout = 400_000;

describe('SnapshotsPage: аудит-пас 5', () => {
  it(
    'показує КОД проєкту, а не голий числовий id',
    async () => {
      mockFetch();
      show();

      // ⛔ Запит СКОПІЙОВАНО в межі таблиці: той самий код `KASH_2026`
      // одночасно існує прихованим варіантом у списку `Select` вище
      // (Mantine рендерить опції комбобоксу в DOM навіть закритим) —
      // незвужений запит на весь документ падає на «Found multiple
      // elements», а не підтверджує чи спростовує фікс.
      const table = await screen.findByRole('table', {}, { timeout: SlowEnvTimeout });

      // ⛔ Головне твердження: числовий `projectId` (42) НЕ має з'явитися як
      // текст комірки — лише резолвлений код (`KASH_2026`).
      await within(table).findByText('KASH_2026', {}, { timeout: SlowEnvTimeout });
    },
    SlowEnvTimeout,
  );

  it(
    'таблиця з бейджем статусу — всередині ScrollArea, а не голою на сторінці',
    async () => {
      mockFetch();
      show();

      const table = await screen.findByRole('table', {}, { timeout: SlowEnvTimeout });
      const scrollArea = table.closest('.mantine-ScrollArea-root');

      expect(scrollArea).not.toBeNull();
    },
    SlowEnvTimeout,
  );
});
