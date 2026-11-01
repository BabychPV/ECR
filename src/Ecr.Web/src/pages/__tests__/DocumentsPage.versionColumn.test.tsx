import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter, useLocation } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { JSX } from 'react';
import { DocumentsPage } from '@/pages/DocumentsPage';
import { testTheme } from '@/test/render';

/**
 * Колонка «Version» переліку документів: версія шаблону за обраний період
 * (`templateVersionByPeriod[період] ?? templateVersion`), сортування й фільтр - клієнтські, в адресі.
 */
const SlowEnvTimeout = 400_000;

const item = (id: number, templateVersion: string | null, templateVersionByPeriod: Record<string, string> | null) => ({
  id,
  businessKey: `DOC-00000${id}`,
  createdAt: '2026-01-01T00:00:00Z',
  projectId: 1,
  sheetCount: 1,
  sheetStates: { S1: 'Draft' },
  hasLateEdits: false,
  templateVersion,
  templateVersionByPeriod,
});

// Порядок сервера: 1 (2.0), 2 (1.0 → 3.0 за 202609), 3 (без версії), 4 (1.0)
const Items = [item(1, '2.0', null), item(2, '1.0', { '202609': '3.0' }), item(3, null, null), item(4, '1.0', null)];

function mockFetch(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      const json = (body: unknown): Response =>
        new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });

      if (url.includes('/api/v1/me')) {
        return json({ denies: [], grants: {}, isSimulation: false, language: 'en', mustChangePassword: false, permissions: [], simulatedForUserId: null, userId: 1, userName: 'tester' });
      }
      if (url.includes('/api/v1/documents/summary')) return json({ draft: 4, submitted: 0, approved: 0, rejected: 0, withIssues: 0 });
      if (url.includes('/api/v1/documents')) return json({ items: Items, nextCursor: null, totalCount: Items.length });
      if (url.includes('/api/v1/projects')) return json({ items: [{ id: 1, code: 'ATR', status: 'Active' }], nextCursor: null, totalCount: 1 });

      return json(null);
    }),
  );
}

function LocationProbe(): JSX.Element {
  const location = useLocation();

  return <span data-testid="location">{location.search}</span>;
}

function show(url: string): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter initialEntries={[url]}>
        <QueryClientProvider client={client}>
          <DocumentsPage />
          <LocationProbe />
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

const rowKeys = (): string[] =>
  Array.from(document.querySelectorAll('[data-document-row]')).map((row) => row.getAttribute('data-document-row') ?? '');

const versionOf = (key: string): string | null =>
  document.querySelector(`[data-document-row="${key}"] [data-document-version]`)?.getAttribute('data-document-version') ?? null;

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('DocumentsPage: колонка «Version»', () => {
  it(
    'показує версію ПЕРІОДУ (карта перекриває типову), а без версії — «—»',
    async () => {
      mockFetch();
      show('/?periodKey=202609');

      await screen.findByText('DOC-000001', {}, { timeout: SlowEnvTimeout });
      expect(versionOf('DOC-000001')).toBe('2.0');
      expect(versionOf('DOC-000002')).toBe('3.0');
      expect(versionOf('DOC-000003')).toBe('none');
      // Підказка «за період» - лише там, де значення з карти.
      const fromMap = document.querySelector('[data-document-row="DOC-000002"] [data-document-version]');
      expect(fromMap?.getAttribute('data-version-source')).toBe('period');
      expect(fromMap?.getAttribute('title')).toBe('⟦documents.versionForPeriod (period=September 2026)⟧');
      expect(document.querySelector('[data-document-row="DOC-000001"] [data-document-version]')?.getAttribute('title')).toBeNull();
    },
    SlowEnvTimeout,
  );

  it(
    'в іншому періоді та сама картка показує типову версію',
    async () => {
      mockFetch();
      show('/?periodKey=202610');

      await screen.findByText('DOC-000002', {}, { timeout: SlowEnvTimeout });
      expect(versionOf('DOC-000002')).toBe('1.0');
    },
    SlowEnvTimeout,
  );

  it(
    'клік по заголовку сортує сторінку (asc → desc → без), `aria-sort` і адреса оновлюються, безверсійний - в кінці',
    async () => {
      mockFetch();
      show('/?periodKey=202609');
      const user = userEvent.setup();

      await screen.findByText('DOC-000001', {}, { timeout: SlowEnvTimeout });
      expect(rowKeys()).toEqual(['DOC-000001', 'DOC-000002', 'DOC-000003', 'DOC-000004']);

      const sortButton = document.querySelector<HTMLButtonElement>('[data-version-sort]');
      expect(sortButton).not.toBeNull();

      await user.click(sortButton!);
      await waitFor(() => expect(rowKeys()).toEqual(['DOC-000004', 'DOC-000001', 'DOC-000002', 'DOC-000003']), {
        timeout: SlowEnvTimeout,
      });
      expect(screen.getByTestId('location').textContent).toContain('vsort=asc');
      expect(document.querySelector('[data-column="version"]')?.getAttribute('aria-sort')).toBe('ascending');

      await user.click(document.querySelector<HTMLButtonElement>('[data-version-sort]')!);
      await waitFor(() => expect(rowKeys()).toEqual(['DOC-000002', 'DOC-000001', 'DOC-000004', 'DOC-000003']), {
        timeout: SlowEnvTimeout,
      });
      expect(document.querySelector('[data-column="version"]')?.getAttribute('aria-sort')).toBe('descending');
    },
    SlowEnvTimeout,
  );

  it(
    'сортування з адреси читається при відкритті (посилання відтворює порядок)',
    async () => {
      mockFetch();
      show('/?periodKey=202609&vsort=desc');

      await screen.findByText('DOC-000001', {}, { timeout: SlowEnvTimeout });
      expect(rowKeys()).toEqual(['DOC-000002', 'DOC-000001', 'DOC-000004', 'DOC-000003']);
    },
    SlowEnvTimeout,
  );

  it(
    'фільтр за версіями (з адреси) лишає точні збіги за версією періоду; «Clear filters» його знімає',
    async () => {
      mockFetch();
      show('/?periodKey=202609&versions=1.0,3.0');
      const user = userEvent.setup();

      await screen.findByText('DOC-000002', {}, { timeout: SlowEnvTimeout });
      expect(rowKeys()).toEqual(['DOC-000002', 'DOC-000004']);

      await user.click(
        await screen.findByRole('button', { name: '⟦documents.clearFilters⟧' }, { timeout: SlowEnvTimeout }),
      );
      await waitFor(() => expect(rowKeys()).toHaveLength(4), { timeout: SlowEnvTimeout });
      expect(screen.getByTestId('location').textContent).toBe('?periodKey=202609');
    },
    SlowEnvTimeout,
  );

  it(
    'фільтр, що нічого не лишив, дає «нічого не знайшлося», а не порожню таблицю',
    async () => {
      mockFetch();
      show('/?periodKey=202609&versions=9.9');

      await screen.findByText('⟦documents.noMatch⟧', {}, { timeout: SlowEnvTimeout });
      expect(rowKeys()).toEqual([]);
      expect(within(document.body).queryByText('DOC-000001')).toBeNull();
    },
    SlowEnvTimeout,
  );
});
