import { describe, it, expect, vi, afterEach } from 'vitest';
import { configure, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { AuditPage } from '@/pages/admin/AuditPage';
import { archiveWindow } from '@/features/audit/api';
import { testTheme } from '@/test/render';

configure({ asyncUtilTimeout: 20_000 });

/**
 * НФ-8.9.6 / D-247: журнал змін комірок читає й архів аудиту; людині треба дати спосіб потрапити в
 * період за межею «гарячого» журналу. Кнопка «Archive» ставить вікно на 90 днів перед цією межею, а
 * рядок із архіву показується з позначкою.
 */

const Hot = {
  changedAt: '2026-09-24T10:00:00Z',
  periodKey: 202609,
  documentId: 1,
  rowKey: 'R1',
  columnDefId: 2,
  oldValue: '1',
  newValue: '2',
  changedByUserId: 3,
  origin: 'UserEdit',
  isLateEdit: false,
  isOutOfWindow: false,
  fromArchive: false,
  changedByDisplayName: 'D. Nurlanova',
  documentBusinessKey: 'AKT-001',
  documentNameL10n: null,
  columnCode: 'CO2',
  columnHeaderL10n: { values: { en: 'CO2 emissions' } },
  columnDataType: 'Decimal',
};

const Archived = {
  ...Hot,
  changedAt: '2024-08-10T10:00:00Z',
  periodKey: 202408,
  rowKey: 'R-ARC',
  fromArchive: true,
};

/** Початок вікна «Archive» як миттєвість запиту: північ названої дати в поясі браузера. */
function windowStartInstant(): string {
  const [y, m, d] = archiveWindow().from.split('-').map(Number) as [number, number, number];

  return new Date(y, m - 1, d).toISOString();
}

function mockFetch(urls: string[]): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      urls.push(url);

      const archiveWindowUrl = url.includes('/api/v1/audit/cells') && url.includes(encodeURIComponent(windowStartInstant()));
      const body = url.includes('/api/v1/me')
        ? { userId: 1, userName: 'auditor', language: 'en', permissions: ['Security.ViewAudit'], grants: {}, denies: [], isSimulation: false, simulatedForUserId: null, mustChangePassword: false }
        : { items: archiveWindowUrl ? [Archived] : [Hot], nextCursor: null, totalCount: null };

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

describe('AuditPage: архів аудиту (НФ-8.9.6)', { timeout: 60_000 }, () => {
  it('кнопка «Archive» ставить вікно перед межею гарячого журналу й показує рядок із позначкою', async () => {
    const urls: string[] = [];
    mockFetch(urls);
    show();

    // Початково — гаряче вікно (останній тиждень), рядок без позначки.
    const hotRow = (await screen.findByText('R1 · CO2 emissions (CO2)')).closest('tr') as HTMLElement;
    expect(hotRow.querySelector('[data-audit-archive]')).toBeNull();

    fireEvent.click(screen.getByText('⟦audit.archiveWindow⟧'));

    // Запит пішов саме за вікном «90 днів перед межею»: північ `from` і північ доби після `to` у поясі браузера.
    const { to } = archiveWindow();
    const [ty, tm, td] = to.split('-').map(Number) as [number, number, number];
    const expectedFrom = windowStartInstant();
    const expectedTo = new Date(ty, tm - 1, td + 1).toISOString();

    await waitFor(() => {
      const cells = urls.filter((u) => u.includes('/api/v1/audit/cells'));
      const last = new URL(cells[cells.length - 1] as string, 'http://localhost');
      expect(last.searchParams.get('from')).toBe(expectedFrom);
      expect(last.searchParams.get('to')).toBe(expectedTo);
    });

    // Рядок з архіву — з позначкою; мутація «прибрати fromArchive у CellChangesTable» лишає його без неї.
    const archivedRow = (await screen.findByText('R-ARC · CO2 emissions (CO2)')).closest('tr') as HTMLElement;
    expect(archivedRow.querySelector('[data-audit-archive]')).not.toBeNull();
  });
});
