import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { AuditPage } from '@/pages/admin/AuditPage';
import { structureChangesQuery } from '@/features/audit/api';
import { testTheme } from '@/test/render';

/**
 * `BE-16`: вкладка структурних змін питає СВІЙ маршрут із вікном сторінки.
 *
 * ⛔ Предмет — адреса запиту, як і в `AuditPage.filters.test.tsx`: вкладка, що
 * намальована й питає не те (або не питає нічого), виглядає як «змін не було».
 */
const page = {
  items: [
    {
      changedAt: '2026-01-05T10:00:00Z',
      changedByUserId: 41,
      changeReason: null,
      entityId: 3,
      entityType: 'cfg.RegistryDef',
      newJson: null,
      oldJson: null,
      operation: 'SaveRules',
    },
  ],
  nextCursor: null,
  totalCount: null,
};

/** Усі адреси аудиту, які клієнт справді запитав. */
function mockFetch(): string[] {
  const seen: string[] = [];

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('/api/v1/audit/')) {
        seen.push(url);
      }

      return new Response(JSON.stringify(url.includes('/api/v1/audit/') ? page : null), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      });
    }),
  );

  return seen;
}

afterEach(() => {
  vi.unstubAllGlobals();
});

const SlowEnvTimeout = 400_000;

describe('structureChangesQuery', () => {
  it('несе вікно завжди, фільтри — лише коли задано', () => {
    const bare = new URLSearchParams(structureChangesQuery({ from: '2026-01-01', to: '2026-01-08' }));
    expect([...bare.keys()].sort()).toStrictEqual(['from', 'limit', 'to']);

    const full = new URLSearchParams(
      structureChangesQuery({
        from: '2026-01-01',
        to: '2026-01-08',
        entityType: 'cfg.RegistryDef',
        changedByUserId: 41,
        cursor: 'abc',
      }),
    );
    expect(full.get('entityType')).toBe('cfg.RegistryDef');
    expect(full.get('changedByUserId')).toBe('41');
    expect(full.get('cursor')).toBe('abc');
  });
});

describe('AuditPage: вкладка структурних змін', () => {
  it(
    'питає /audit/structure з вікном сторінки і НЕ питає журнал комірок',
    async () => {
      const seen = mockFetch();
      const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

      render(
        <MantineProvider theme={testTheme}>
          <MemoryRouter
            initialEntries={[
              '/admin/audit?view=structure&from=2026-01-01&to=2026-01-08&entityType=cfg.RegistryDef&changedBy=41',
            ]}
          >
            <QueryClientProvider client={client}>
              <AuditPage />
            </QueryClientProvider>
          </MemoryRouter>
        </MantineProvider>,
      );

      await screen.findByText('SaveRules', {}, { timeout: SlowEnvTimeout });

      // ⛔ Мутація: прибрати `!structure` з `useCellChanges(filter, !structure)`
      // — тут з'явиться другий запит, по партиціях `aud.CellChange`, заради
      // таблиці, якої на вкладці не видно.
      expect(seen).toHaveLength(1);
      expect(seen[0]).toContain('/api/v1/audit/structure?');

      const query = new URLSearchParams(seen[0]!.split('?')[1]);
      expect(query.get('from')).toBe('2026-01-01');
      expect(query.get('to')).toBe('2026-01-08');
      expect(query.get('entityType')).toBe('cfg.RegistryDef');
      expect(query.get('changedByUserId')).toBe('41');
    },
    SlowEnvTimeout,
  );
});
