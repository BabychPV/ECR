import { describe, it, expect, vi, afterEach } from 'vitest';
import { configure, render, screen, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { AuditPage, auditValueText } from '@/pages/admin/AuditPage';
import { testTheme } from '@/test/render';

configure({ asyncUtilTimeout: 20_000 });

/**
 * `R-18`/`X-35`: журнал змін показував «By user 3 · Document 1 · R1 · 2» і
 * значення «Was 53.1771000000000000» — номери й формат сховища замість того,
 * що людина набирала й шукає.
 */

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

describe('AuditPage: журнал називає, а не нумерує', { timeout: 60_000 }, () => {
  it('автор, документ, колонка — іменами; значення — без хвоста сховища', async () => {
    mockFetch([Change]);
    show();

    const cell = await screen.findByText('D. Nurlanova');
    const row = cell.closest('tr') as HTMLElement;

    // ⛔ Мутація «повернути `{change.changedByUserId}`» дає «3».
    expect(within(row).getByText('AKT-001')).toBeTruthy();
    expect(within(row).getByText('R1 · CO2 emissions (CO2)')).toBeTruthy();

    // ⛔ Мутація «повернути `{change.oldValue}`» дає «53.1771000000000000».
    expect(within(row).getByText('53.1771')).toBeTruthy();
    expect(within(row).getByText('54.2')).toBeTruthy();
    expect(within(row).queryByText('53.1771000000000000')).toBeNull();
  });

  it('зниклий автор — чесне «користувач #id», а не порожнеча', async () => {
    mockFetch([{ ...Change, changedByDisplayName: null }]);
    show();

    expect(await screen.findByText('⟦audit.userGone (id=3)⟧')).toBeTruthy();
  });

  it('фільтр автора — вибір за іменем, а не поле для номера', async () => {
    mockFetch([Change]);
    show();

    await screen.findByText('AKT-001');

    const author = screen.getByRole('textbox', { name: '⟦audit.author⟧' });

    // ⛔ Мутація «повернути `NumberInput`» — у полі немає переліку (`aria-haspopup`).
    expect(author.getAttribute('aria-haspopup')).toBe('listbox');
  });
});

describe('auditValueText: правило показу за типом колонки', () => {
  it.each([
    ['53.1771000000000000', 'Decimal', '53.1771'],
    ['1200.5000000000000000', 'Formula', '1,200.5'],
    ['42', 'Int', '42'],
    ['abc', 'Decimal', 'abc'],
    ['R1-code', 'String', 'R1-code'],
    [null, 'Decimal', '—'],
  ])('%s (%s) → %s', (value, type, shown) => {
    expect(auditValueText(value, type)).toBe(shown);
  });

  it('дата — мовою інтерфейсу', () => {
    expect(auditValueText('2026-03-01', 'Date')).toBe('Mar 1, 2026');
  });
});
