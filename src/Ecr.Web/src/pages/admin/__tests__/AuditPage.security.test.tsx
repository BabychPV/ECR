import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { AuditPage } from '@/pages/admin/AuditPage';
import { securityEventsQuery } from '@/features/audit/api';
import { testTheme } from '@/test/render';

/**
 * ФВ-5.24: вкладка подій безпеки питає СВІЙ маршрут `/audit/security` із вікном сторінки й показує
 * відмову в доступі (`AccessDenied`) — читач журналу, якого раніше не було.
 *
 * ⛔ Предмет — адреса запиту (як у `AuditPage.structure.test.tsx`): вкладка, що намальована, але питає
 * не те, виглядає як «подій не було».
 */
const page = {
  items: [
    {
      changedAt: '2026-01-05T10:00:00Z',
      eventType: 'AccessDenied',
      targetUserId: null,
      targetRoleId: null,
      detailsJson: '{"code":"ECR-AUTH-0403","method":"GET","route":"api/v1/audit/security"}',
      changedByUserId: 41,
      changedByDisplayName: 'Intruder',
      correlationId: 'corr-123',
    },
  ],
  nextCursor: null,
  totalCount: null,
};

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

describe('securityEventsQuery', () => {
  it('несе вікно завжди, фільтри — лише коли задано', () => {
    const bare = new URLSearchParams(securityEventsQuery({ from: '2026-01-01', to: '2026-01-08' }));
    expect([...bare.keys()].sort()).toStrictEqual(['from', 'limit', 'to']);

    const full = new URLSearchParams(
      securityEventsQuery({
        from: '2026-01-01',
        to: '2026-01-08',
        eventType: 'AccessDenied',
        changedByUserId: 41,
        cursor: 'abc',
      }),
    );
    expect(full.get('eventType')).toBe('AccessDenied');
    expect(full.get('changedByUserId')).toBe('41');
    expect(full.get('cursor')).toBe('abc');
  });
});

describe('AuditPage: вкладка подій безпеки', () => {
  it(
    'питає /audit/security з вікном сторінки, показує подію й НЕ питає журнал комірок',
    async () => {
      const seen = mockFetch();
      const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

      render(
        <MantineProvider theme={testTheme}>
          <MemoryRouter
            initialEntries={['/admin/audit?view=security&from=2026-01-01&to=2026-01-08&eventType=AccessDenied&changedBy=41']}
          >
            <QueryClientProvider client={client}>
              <AuditPage />
            </QueryClientProvider>
          </MemoryRouter>
        </MantineProvider>,
      );

      await screen.findByText('corr-123', {}, { timeout: SlowEnvTimeout });
      expect(screen.getAllByText('AccessDenied').length).toBeGreaterThan(0);
      expect(screen.getByText('Intruder')).toBeTruthy();

      // ⛔ Мутація: у `AuditPage` замінити `!other` на `!structure` в `useCellChanges(filter, !other)` —
      // з'явиться другий запит (по партиціях `aud.CellChange`) заради невидимої таблиці.
      expect(seen).toHaveLength(1);
      expect(seen[0]).toContain('/api/v1/audit/security?');

      const query = new URLSearchParams(seen[0]!.split('?')[1]);
      expect(query.get('from')).toBe(new Date(2026, 0, 1).toISOString());
      expect(query.get('to')).toBe(new Date(2026, 0, 9).toISOString());
      expect(query.get('eventType')).toBe('AccessDenied');
      expect(query.get('changedByUserId')).toBe('41');
    },
    SlowEnvTimeout,
  );
});
