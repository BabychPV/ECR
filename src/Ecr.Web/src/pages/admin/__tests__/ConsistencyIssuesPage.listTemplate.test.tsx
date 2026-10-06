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

const requested: string[] = [];

/** Розклад за вагою, як його рахує СЕРВЕР по всьому журналу (LS-E). */
const Totals = { errors: 12, warnings: 4, info: 1 };

function respondWith(page: unknown, permissions: string[] = []): void {
  requested.length = 0;
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      requested.push(url);
      const body = url.endsWith('/api/v1/me')
        ? { permissions }
        : url.includes('/consistency/summary')
          ? { ...Totals, total: 17, lastDetectedAt: '2026-10-05T21:00:00Z' }
          : page;
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

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('severityState', () => {
  it('1 → Info, 2 → Warning, решта → Error (деградація в бік уваги)', () => {
    expect([1, 2, 3, 4].map(severityState)).toEqual(['Info', 'Warning', 'Error', 'Error']);
  });
});

describe('ConsistencyIssuesPage на шаблоні переліку (UI-20)', () => {
  it('пояснення під заголовком; смуга — розклад СЕРВЕРА (`totals`), не лічба сторінки', async () => {
    respondWith({ items: Findings, nextCursor: null, totalCount: 17, totals: Totals });
    show();

    await screen.findByText('Знахідка 1');
    // Рівно одне пояснення під заголовком — у рядку пояснення, не другим `meta` (batch-2-a, дефект 3).
    expect(screen.getAllByTestId('page-description').map((node) => node.textContent)).toEqual(['⟦consistency.description⟧']);
    expect(screen.getAllByText('⟦consistency.description⟧')).toHaveLength(1);

    const strip = screen.getByRole('group', { name: '⟦consistency.statsLabel⟧' });
    const value = (label: string): string =>
      within(strip).getByText(label).closest('[data-stat]')?.querySelector('[data-stat-value]')?.textContent ?? '';

    // ⛔ Мутаційний доказ: поверніть лічбу по завантаженій сторінці — буде
    // 3/1/1 замість 12/4/1, тобто перша сотня видаватиме себе за весь журнал.
    expect(value('⟦consistency.statErrors⟧')).toBe('12');
    expect(value('⟦consistency.statWarnings⟧')).toBe('4');
    expect(value('⟦consistency.statInfo⟧')).toBe('1');
  });

  it('є наступна сторінка — смуга лишається: числа з сервера, а не з першої сотні', async () => {
    respondWith({ items: Findings, nextCursor: 'c2', totalCount: 17, totals: Totals });
    show();

    await screen.findByText('Знахідка 1');
    expect(screen.getByRole('group', { name: '⟦consistency.statsLabel⟧' })).toBeTruthy();
  });

  it('відповіді з підсумками ще немає — смуги немає, а не нулі', async () => {
    respondWith({ items: Findings, nextCursor: null, totalCount: null, totals: null });
    show();

    await screen.findByText('Знахідка 1');

    // ⛔ Мутаційний доказ: замініть умову на `totals ?? { errors: 0, … }` —
    // з'явиться смуга з нулями, тобто «помилок немає», яких ніхто не рахував.
    expect(screen.queryByRole('group', { name: '⟦consistency.statsLabel⟧' })).toBeNull();
  });

  it('клац по показнику просить сервер відфільтрувати вагу і пише її в адресу', async () => {
    respondWith({ items: Findings, nextCursor: null, totalCount: 17, totals: Totals });
    show();

    await screen.findByText('Знахідка 1');
    fireEvent.click(screen.getByRole('button', { name: /⟦consistency.statWarnings⟧/ }));

    // ⚠ Вага — фільтр СЕРВЕРА (LS-E): журнал курсорний, і фільтр по
    // завантаженій сотні пропускав би решту. Код ваги — 2 (`ValidationSeverity`).
    await waitFor(() => {
      expect(requested.some((url) => url.includes('/consistency/issues') && url.includes('&severity=2'))).toBe(true);
    });
    expect(location).toContain('severity=Warning');
  });

  it('шторка знахідки — кнопкою в рядку, адреса ?panel=issue-<id>', async () => {
    respondWith({ items: Findings, nextCursor: null, totalCount: 17, totals: Totals });
    show();

    fireEvent.click(await screen.findByRole('button', { name: 'Знахідка 3' }));

    const drawer = await screen.findByRole('dialog', { name: /RULE_3/ });
    expect(location).toContain('panel=issue-3');
    expect(await within(drawer).findByText('doc.TableRow · 4003')).toBeTruthy();
  });

  it('число в шапці — весь журнал за прапорцем (`GET /consistency/summary`)', async () => {
    respondWith({ items: Findings, nextCursor: 'c2', totalCount: 17, totals: Totals });
    show();

    await screen.findByText('Знахідка 1');
    await waitFor(() => {
      expect(requested.some((url) => url.endsWith('/api/v1/consistency/summary?openOnly=true'))).toBe(true);
    });
    expect(await screen.findByText('17')).toBeTruthy();
  });

  it('«Run check now» — головна дія шапки, лише з правом System.RunJob', async () => {
    respondWith({ items: Findings, nextCursor: null, totalCount: 17, totals: Totals }, []);
    show();

    await screen.findByText('Знахідка 1');
    expect(screen.queryByRole('button', { name: '⟦consistency.runNow⟧' })).toBeNull();
  });
});
