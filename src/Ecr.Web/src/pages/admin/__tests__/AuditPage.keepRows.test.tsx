import { describe, it, expect, vi, afterEach } from 'vitest';
import { configure, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { AuditPage } from '@/pages/admin/AuditPage';
import { testTheme } from '@/test/render';

configure({ asyncUtilTimeout: 20_000 });

/**
 * `R-18`, затримка друку у фільтрі журналу (живцем 72–117 мс): кожен
 * застосований debounce клав скелет на місце таблиці й будував її наново.
 * Тепер попередня сторінка лишається, доки їде нова.
 */

const row = {
  changedAt: '2026-09-24T10:00:00Z', periodKey: 202609, documentId: 1, rowKey: 'R1', columnDefId: 2,
  oldValue: '1', newValue: '2', changedByUserId: 3, origin: 'UserEdit', isLateEdit: false,
  changedByDisplayName: 'D. Nurlanova', documentBusinessKey: 'AKT-001', columnCode: 'CO2', columnDataType: 'Decimal',
};

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('AuditPage: зміна фільтра не прибирає таблицю', { timeout: 60_000 }, () => {
  it('поки їде нова сторінка — на екрані попередня, а не скелет', async () => {
    let release: (() => void) | null = null;

    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL) => {
        const url = String(input);
        const json = (body: unknown): Response =>
          new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });

        if (url.includes('/api/v1/me')) {
          return json({ userId: 1, userName: 'a', language: 'en', permissions: [], grants: {}, denies: [], isSimulation: false, simulatedForUserId: null, mustChangePassword: false });
        }

        if (url.includes('documentId=')) {
          // Друга сторінка «в дорозі», доки тест її не відпустить.
          await new Promise<void>((resolve) => {
            release = resolve;
          });
        }

        return json({ items: [row], nextCursor: null, totalCount: null });
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

    expect(await screen.findByText('AKT-001')).toBeTruthy();

    fireEvent.change(screen.getByRole('textbox', { name: /audit\.document/ }), { target: { value: '7' } });

    // Застосований фільтр дійшов до запиту…
    await waitFor(() => expect(release).not.toBeNull());

    // ⛔ …а таблиця на місці. Мутація «`data={changes.data}`» (без
    // `useLastData`) дає тут скелет і жодного рядка.
    expect(screen.getByText('AKT-001')).toBeTruthy();

    (release as unknown as () => void)();
  });
});
