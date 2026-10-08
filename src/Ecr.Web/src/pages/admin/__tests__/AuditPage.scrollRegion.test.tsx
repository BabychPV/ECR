import { describe, it, expect, vi, afterEach } from 'vitest';
import { configure, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { AuditPage } from '@/pages/admin/AuditPage';
import { testTheme } from '@/test/render';

configure({ asyncUtilTimeout: 20_000 });

/** RC14-C: список журналу прокручується сам (висота ~500px), а не розтягує сторінку. */

const row = {
  changedAt: '2026-09-24T10:00:00Z', periodKey: 202609, documentId: 1, rowKey: 'R1', columnDefId: 2,
  oldValue: '1', newValue: '2', changedByUserId: 3, origin: 'UserEdit', isLateEdit: false,
  changedByDisplayName: 'D. Nurlanova', documentBusinessKey: 'AKT-001', columnCode: 'CO2', columnDataType: 'Decimal',
};

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('AuditPage: прокрутка списку', { timeout: 60_000 }, () => {
  it('таблиця змін лежить у власній прокрутній області з обмеженою висотою', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL) => {
        const body = String(input).includes('/api/v1/me')
          ? { userId: 1, userName: 'a', language: 'en', permissions: [], grants: {}, denies: [], isSimulation: false, simulatedForUserId: null, mustChangePassword: false }
          : { items: [row], nextCursor: null, totalCount: null };

        return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
      }),
    );

    render(
      <MantineProvider theme={testTheme}>
        <MemoryRouter initialEntries={['/admin/audit']}>
          <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
            <AuditPage />
          </QueryClientProvider>
        </MemoryRouter>
      </MantineProvider>,
    );

    await screen.findByText('D. Nurlanova');

    // ⛔ Мутаційний доказ: прибери обгортку `data-audit-scroll` у AuditPage — тут червоне.
    const region = document.querySelector<HTMLElement>('[data-audit-scroll]');
    expect(region).not.toBeNull();
    expect(region?.style.overflow).toBe('auto');
    expect(region?.querySelector('table')).not.toBeNull();
    expect(region?.getAttribute('tabindex')).toBe('0');
  });
});
