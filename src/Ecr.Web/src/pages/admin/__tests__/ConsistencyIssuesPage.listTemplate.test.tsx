import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, fireEvent, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter, useLocation } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { JSX } from 'react';
import { ConsistencyIssuesPage, severityState } from '@/pages/admin/ConsistencyIssuesPage';

/**
 * UI-20: журнал узгодженості на шаблоні переліку (макет `screens-ops.js`
 * `/admin/consistency`, `35-consistency.png`).
 *
 * Стережеться: пояснення під заголовком; смуга за вагою — лише над ПОВНИМ
 * результатом; клац по показнику фільтрує; шторка `?panel=issue-<id>`
 * відкривається кнопкою з клавіатури; «Run check now» — головна дія шапки
 * лише з правом.
 */

const issue = (id: number, severity: number, resolvedAt: string | null = null): Record<string, unknown> => ({
  id,
  detectedAt: '2026-10-05T21:00:00Z',
  severity,
  ruleCode: `RULE_${String(id)}`,
  entityType: 'doc.TableRow',
  entityId: 4000 + id,
  message: `Знахідка ${String(id)}`,
  resolvedAt,
  resolvedByUserId: null,
});

const Findings = [issue(1, 3), issue(2, 3), issue(3, 2), issue(4, 1), issue(5, 3, '2026-10-06T03:00:00Z')];

function respondWith(page: unknown, permissions: string[] = []): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      const body = url.endsWith('/api/v1/me') ? { permissions } : page;
      return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
    }),
  );
}

let location = '';

function LocationProbe(): JSX.Element | null {
  location = useLocation().search;
  return null;
}

function show(entry = '/admin/consistency'): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider>
      <MemoryRouter initialEntries={[entry]}>
        <QueryClientProvider client={client}>
          <ConsistencyIssuesPage />
          <LocationProbe />
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

function shownIds(): string[] {
  return Array.from(document.querySelectorAll('[data-issue-open]')).map((node) => node.getAttribute('data-issue-open') ?? '');
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('severityState', () => {
  it('1 → Info, 2 → Warning, решта → Error (деградація в бік уваги)', () => {
    expect([1, 2, 3, 4].map(severityState)).toEqual(['Info', 'Warning', 'Error', 'Error']);
  });
});

describe('ConsistencyIssuesPage на шаблоні переліку (UI-20)', () => {
  it('пояснення під заголовком; смуга рахує НЕРОЗВ’ЯЗАНІ за вагою', async () => {
    respondWith({ items: Findings, nextCursor: null, totalCount: null });
    show();

    await screen.findByText('Знахідка 1');
    expect(screen.getByText('⟦consistency.description⟧')).toBeTruthy();

    const strip = screen.getByRole('group', { name: '⟦consistency.statsLabel⟧' });
    const value = (label: string): string =>
      within(strip).getByText(label).closest('[data-stat]')?.querySelector('[data-stat-value]')?.textContent ?? '';

    // ⛔ Мутаційний доказ: приберіть фільтр `resolvedAt === null` — помилок
    // стане 3 (розв'язана №5 теж рахувалася б як така, що вимагає дії).
    expect(value('⟦consistency.statErrors⟧')).toBe('2');
    expect(value('⟦consistency.statWarnings⟧')).toBe('1');
    expect(value('⟦consistency.statInfo⟧')).toBe('1');
  });

  it('є наступна сторінка — смуги НЕМАЄ: перша сотня не видає себе за весь журнал', async () => {
    respondWith({ items: Findings, nextCursor: 'c2', totalCount: null });
    show();

    await screen.findByText('Знахідка 1');

    // ⛔ Мутаційний доказ: `complete = page !== undefined` без умови про
    // `nextCursor` — смуга з'явиться з числами лише першої сторінки.
    expect(screen.queryByRole('group', { name: '⟦consistency.statsLabel⟧' })).toBeNull();
  });

  it('клац по показнику фільтрує перелік і пише вагу в адресу', async () => {
    respondWith({ items: Findings, nextCursor: null, totalCount: null });
    show();

    await screen.findByText('Знахідка 1');
    fireEvent.click(screen.getByRole('button', { name: /⟦consistency.statWarnings⟧/ }));

    await waitFor(() => {
      expect(shownIds()).toEqual(['3']);
    });
    expect(location).toContain('severity=Warning');
  });

  it('шторка знахідки — кнопкою в рядку, адреса ?panel=issue-<id>', async () => {
    respondWith({ items: Findings, nextCursor: null, totalCount: null });
    show();

    fireEvent.click(await screen.findByRole('button', { name: 'Знахідка 3' }));

    const drawer = await screen.findByRole('dialog', { name: /RULE_3/ });
    expect(location).toContain('panel=issue-3');
    expect(await within(drawer).findByText('doc.TableRow · 4003')).toBeTruthy();
  });

  it('«Run check now» — головна дія шапки, лише з правом System.RunJob', async () => {
    respondWith({ items: Findings, nextCursor: null, totalCount: null }, []);
    show();

    await screen.findByText('Знахідка 1');
    expect(screen.queryByRole('button', { name: '⟦consistency.runNow⟧' })).toBeNull();
  });
});
