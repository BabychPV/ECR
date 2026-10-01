import { describe, it, expect, vi, afterEach } from 'vitest';
import { configure, render, screen, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { AuditPage } from '@/pages/admin/AuditPage';
import { testTheme } from '@/test/render';

configure({ asyncUtilTimeout: 20_000 });

/** ФВ-2.16 / D-239: правка за політикою Warn поза вікном доступу позначена в журналі. */

const Change = {
  changedAt: '2026-09-24T10:00:00Z',
  periodKey: 202609,
  documentId: 1,
  rowKey: 'R1',
  columnDefId: 2,
  oldValue: '53.1771000000000000',
  newValue: '54.2000000000000000',
  changedByUserId: 3,
  origin: 'UserEdit',
  isLateEdit: false,
  isOutOfWindow: false,
  changedByDisplayName: 'D. Nurlanova',
  documentBusinessKey: 'AKT-001',
  documentNameL10n: null,
  columnCode: 'CO2',
  columnHeaderL10n: { values: { en: 'CO2 emissions' } },
  columnDataType: 'Decimal',
};

function mockFetch(items: unknown[]): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const body = String(input).includes('/api/v1/me')
        ? { userId: 1, userName: 'auditor', language: 'en', permissions: ['Security.ViewAudit'], grants: {}, denies: [], isSimulation: false, simulatedForUserId: null, mustChangePassword: false }
        : { items, nextCursor: null, totalCount: null };

      return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
    }),
  );
}

function show(): void {
  render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter initialEntries={['/admin/audit']}>
        <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
          <AuditPage />
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('AuditPage: позначка «поза вікном»', { timeout: 60_000 }, () => {
  it('isOutOfWindow=true показує позначку, false - ні', async () => {
    mockFetch([{ ...Change, isOutOfWindow: true }, { ...Change, rowKey: 'R2', isOutOfWindow: false }]);
    show();

    const marked = (await screen.findByText('R1 · CO2 emissions (CO2)')).closest('tr') as HTMLElement;
    const plain = (await screen.findByText('R2 · CO2 emissions (CO2)')).closest('tr') as HTMLElement;

    // Мутація «прибрати гілку isOutOfWindow» лишає marked без позначки.
    expect(within(marked).getByText('⟦audit.outOfWindow⟧')).toBeTruthy();
    expect(within(plain).queryByText('⟦audit.outOfWindow⟧')).toBeNull();
  });
});