import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter, useLocation } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { JSX } from 'react';
import { AuditPage } from '@/pages/admin/AuditPage';
import { columnOptions } from '@/features/audit/filterOptions';
import { testTheme } from '@/test/render';

/**
 * audit-filters-followup AC-11 (людина 26.11, живий прохід): «після вибору з списку на 1 сек бачу айді» — Column показував
 * «#110545», поки журнал перезавантажувався з новим фільтром (сторінок із цією колонкою на мить немає).
 * Відповідь на запит із `columnDefId` тут ЗАТРИМАНА й порожня: поле має весь цей час показувати назву обраного пункту.
 */
const SlowEnvTimeout = 120_000;
const json = (body: unknown, status = 200): Response =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });

const OilLabel = 'Fuel / Oil / October (M10)';

function row(columnDefId: number, table: string): Record<string, unknown> {
  return {
    changedAt: '2026-11-20T10:00:00Z',
    periodKey: 202611,
    documentId: 96,
    rowKey: 'Methane',
    columnDefId,
    oldValue: '1',
    newValue: '2',
    changedByUserId: 5,
    origin: 'UserEdit',
    isLateEdit: false,
    isOutOfWindow: false,
    isOverwrite: false,
    fromArchive: false,
    changedByDisplayName: 'Ann',
    documentBusinessKey: 'P1-V9-0001',
    documentNameL10n: { values: { en: 'LAND-TEST-ALL-METHODOLOGIES' } },
    columnCode: 'M10',
    columnHeaderL10n: { values: { en: 'October' } },
    sheetCode: 'S1',
    sheetNameL10n: { values: { en: 'Fuel' } },
    tableCode: table,
    tableNameL10n: { values: { en: table } },
  };
}

function serve(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = new URL(String(input), 'http://x');
      if (url.pathname === '/api/v1/me') {
        return json({ userId: 1, userName: 'a', language: 'en', permissions: ['Security.ViewAudit'] });
      }
      if (url.pathname === '/api/v1/audit/authors') return json([{ userId: 5, displayName: 'Ann' }]);
      if (url.pathname === '/api/v1/documents') return json({ items: [], nextCursor: null });
      if (url.pathname === '/api/v1/audit/cells') {
        if (url.searchParams.has('columnDefId')) {
          await new Promise((resolve) => setTimeout(resolve, 1500));
          return json({ items: [], nextCursor: null, totalCount: 0 });
        }
        return json({ items: [row(73809, 'Gas'), row(73810, 'Oil')], nextCursor: null, totalCount: 2 });
      }
      return json(null);
    }),
  );
}

let location = '';
function Where(): JSX.Element | null {
  location = useLocation().search;
  return null;
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('AuditPage: обраний пункт не показується як «#id» під час перезавантаження', () => {
  it(
    'AC-11: Column тримає назву обраного пункту, поки відповідь затримана й порожня',
    async () => {
      serve();
      render(
        <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
          <MantineProvider theme={testTheme}>
            <MemoryRouter initialEntries={['/admin/audit?from=2026-11-18&to=2026-11-25&documentId=96']}>
              <AuditPage />
              <Where />
            </MemoryRouter>
          </MantineProvider>
        </QueryClientProvider>,
      );
      const user = userEvent.setup();

      const field = (await screen.findByRole('textbox', { name: '⟦audit.columnDefId⟧' })) as HTMLInputElement;
      await user.click(field);
      await user.click(await screen.findByRole('option', { name: OilLabel }));
      await waitFor(() => expect(location).toContain('columnDefId=73810'));

      // Одразу після вибору і весь час, поки йде затримана порожня відповідь: назва, а не «#73810».
      expect(field.value).toBe(OilLabel);
      await new Promise((resolve) => setTimeout(resolve, 1000));
      expect(field.value).toBe(OilLabel);
      expect(field.value).not.toContain('#');
      await new Promise((resolve) => setTimeout(resolve, 1200));
      expect(field.value).toBe(OilLabel);
    },
    SlowEnvTimeout,
  );
});

describe('columnOptions: мітка щойно обраного', () => {
  it('обрана колонка без рядків у журналі лишається з підписом, без підпису — «#id»', () => {
    const picked = { value: '73810', label: OilLabel };
    expect(columnOptions([], 96, 73810, picked)).toEqual([picked]);
    expect(columnOptions([], 96, 73810, null)).toEqual([{ value: '73810', label: '#73810' }]);
    // Мітка іншого пункту обраному не належить.
    expect(columnOptions([], 96, 5, picked)).toEqual([{ value: '5', label: '#5' }]);
  });
});
