import type { JSX } from 'react';
import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { testTheme } from '@/test/render';

/**
 * `CreateDocumentModal` — за `import()`, не статичним імпортом (`D-132`,
 * перф-рефакторинг «bundle splitting»).
 *
 * ⚠ Той самий доказ, що й `features/search/__tests__/SearchLauncher.lazy.
 * test.tsx`: фабрика `vi.mock` виконується, коли модуль ІМПОРТУЮТЬ уперше.
 * Статичний `import { CreateDocumentModal } from '...'` обчислив би модуль
 * разом із самою сторінкою переліку документів — тобто ще до першого кліку на
 * «Створити», — і перша перевірка нижче почервоніла б.
 *
 * ⛔ `fireEvent.click`, НЕ `userEvent.click`: у цьому проєкті задокументована
 * пастка — `userEvent` не завжди ловить лінивий `import()` під моком (той
 * самий застережний коментар стоїть у сусідніх тестах на лінивість).
 */
const probe = vi.hoisted(() => ({ evaluated: false }));

vi.mock('@/features/documents/CreateDocumentModal', () => {
  probe.evaluated = true;

  return {
    CreateDocumentModal: ({ opened }: { opened: boolean }): JSX.Element | null =>
      opened ? <div role="dialog" aria-label="create-stub" /> : null,
  };
});

import { DocumentsPage } from '@/pages/DocumentsPage';

const document_ = {
  id: 1,
  businessKey: 'P1-V1-0001',
  createdAt: '2026-01-01T00:00:00Z',
  projectId: 1,
  sheetCount: 1,
  sheetStates: {},
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
            permissions: ['Document.Create'],
            simulatedForUserId: null,
            userId: 1,
            userName: 'tester',
          }),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        );
      }

      if (url.includes('/api/v1/documents')) {
        return new Response(
          JSON.stringify({ items: [document_], nextCursor: null, totalCount: 1 }),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        );
      }

      if (url.includes('/api/v1/projects')) {
        return new Response(JSON.stringify({ items: [], nextCursor: null, totalCount: 0 }), {
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
      <MemoryRouter initialEntries={['/']}>
        <QueryClientProvider client={client}>
          <DocumentsPage />
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

const SlowEnvTimeout = 400_000;

describe('DocumentsPage: CreateDocumentModal лінивий (D-132)', () => {
  it(
    'модуль діалогу не обчислено до першого кліку на «Створити»',
    async () => {
      mockFetch();
      show();

      const button = await screen.findByRole(
        'button',
        { name: '⟦documents.create⟧' },
        { timeout: SlowEnvTimeout },
      );

      expect(probe.evaluated).toBe(false);
      expect(screen.queryByRole('dialog')).toBeNull();

      fireEvent.click(button);

      expect(await screen.findByRole('dialog', { name: 'create-stub' })).toBeTruthy();
      expect(probe.evaluated).toBe(true);
    },
    SlowEnvTimeout,
  );
});
