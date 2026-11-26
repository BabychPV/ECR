import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter, useLocation } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { JSX } from 'react';
import { AuditPage } from '@/pages/admin/AuditPage';
import { testTheme } from '@/test/render';

/**
 * audit-filters-live D2/D3/D4 (AC-3, AC-4, AC-5): «Document» і «Column» — вибором за підписом, «By user» розрізняє
 * однакові імена.
 * ⛔ Червоний на 7afb19c97: «P1-V9-0001» у Document ставав `documentId=190001`, «M10» у Column — `columnDefId=10`,
 * а два акаунти «b12 window temporary (disable after)» в «By user» були однаковими пунктами.
 */
const SlowEnvTimeout = 120_000;
const TempName = 'b12 window temporary (disable after)';
const json = (body: unknown, status = 200): Response =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });

function row(columnDefId: number, columnCode: string, table: string): Record<string, unknown> {
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
    changedByDisplayName: TempName,
    documentBusinessKey: 'P1-V9-0001',
    documentNameL10n: { values: { en: 'LAND-TEST-ALL-METHODOLOGIES' } },
    columnCode,
    columnHeaderL10n: { values: { en: 'October' } },
    sheetCode: 'S1',
    sheetNameL10n: { values: { en: 'Fuel' } },
    tableCode: table,
    tableNameL10n: { values: { en: table } },
  };
}

const requests: URL[] = [];

/** ✎ AC-8: відповідь `GET /audit/cells/columns`; `null` — як до сервера (ендпоінта немає, `json(null)`). */
let changedColumns: unknown = null;

function serve(): void {
  requests.length = 0;
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = new URL(String(input), 'http://x');
      requests.push(url);
      if (url.pathname === '/api/v1/audit/cells/columns') return json(changedColumns);
      if (url.pathname === '/api/v1/me') {
        return json({ userId: 1, userName: 'a', language: 'en', permissions: ['Security.ViewAudit'] });
      }
      if (url.pathname === '/api/v1/audit/authors') {
        return json([
          { userId: 5, displayName: TempName },
          { userId: 6, displayName: TempName },
        ]);
      }
      if (url.pathname === '/api/v1/documents') {
        return json({
          items: [{ id: 96, projectId: 1, businessKey: 'P1-V9-0001', nameL10n: { values: { en: 'LAND-TEST-ALL-METHODOLOGIES' } } }],
          nextCursor: null,
        });
      }
      if (url.pathname === '/api/v1/audit/cells') {
        return json({ items: [row(73809, 'M10', 'Gas'), row(73810, 'M10', 'Oil')], nextCursor: null, totalCount: 2 });
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

function renderAt(search: string): void {
  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <MantineProvider theme={testTheme}>
        <MemoryRouter initialEntries={[`/admin/audit?from=2026-11-18&to=2026-11-25${search}`]}>
          <AuditPage />
          <Where />
        </MemoryRouter>
      </MantineProvider>
    </QueryClientProvider>,
  );
}

const cellRequests = (): URL[] => requests.filter((url) => url.pathname === '/api/v1/audit/cells');

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  changedColumns = null;
});

describe('AuditPage: вибір документа, колонки й автора за підписом', () => {
  it(
    'AC-3: код документа шукає й обирає документ, а не стає іншим номером',
    async () => {
      serve();
      renderAt('');
      const user = userEvent.setup();

      const field = await screen.findByRole('textbox', { name: '⟦audit.document⟧' });
      await user.type(field, 'P1-V9-0001');
      // Пауза довша за дебаунс фільтра: на базі тут ішов запит `documentId=190001`.
      await new Promise((resolve) => setTimeout(resolve, 700));
      expect(location).not.toContain('documentId=');
      expect(cellRequests().some((url) => url.searchParams.has('documentId'))).toBe(false);
      const searched = requests.filter((url) => url.pathname === '/api/v1/documents').map((url) => url.search);
      expect(searched).toContain('?limit=20&q=P1-V9-0001');

      await user.click(await screen.findByRole('option', { name: /LAND-TEST-ALL-METHODOLOGIES · P1-V9-0001/ }));
      await waitFor(() => expect(location).toContain('documentId=96'));
    },
    SlowEnvTimeout,
  );

  it(
    'AC-4: колонка — за підписом таблиці й заголовка; «M10» не стає колонкою 10',
    async () => {
      serve();
      renderAt('&documentId=96');
      const user = userEvent.setup();

      const field = await screen.findByRole('textbox', { name: '⟦audit.columnDefId⟧' });
      await waitFor(() => expect(cellRequests().length).toBeGreaterThan(0));
      await user.type(field, 'M10');
      await new Promise((resolve) => setTimeout(resolve, 700));
      expect(location).not.toContain('columnDefId=');
      expect(cellRequests().some((url) => url.searchParams.get('columnDefId') === '10')).toBe(false);

      // Два «M10» різних таблиць — різні пункти; обраний дає Id саме своєї колонки.
      expect(await screen.findByRole('option', { name: 'Fuel / Gas / October (M10)' })).toBeTruthy();
      await user.click(await screen.findByRole('option', { name: 'Fuel / Oil / October (M10)' }));
      await waitFor(() => expect(location).toContain('columnDefId=73810'));
    },
    SlowEnvTimeout,
  );

  it(
    'AC-8: «Column» — колонки документа з сервера, і ті, яких немає на завантажених сторінках',
    async () => {
      // Мутація (локально): не передати `useChangedColumns` у `columnOptions` в `AuditPage` — «Fuel / Steam / …» немає.
      changedColumns = [
        {
          columnDefId: 73999,
          columnCode: 'M11',
          columnHeaderL10n: { values: { en: 'November' } },
          sheetCode: 'S1',
          sheetNameL10n: { values: { en: 'Fuel' } },
          tableCode: 'Steam',
          tableNameL10n: { values: { en: 'Steam' } },
        },
      ];
      serve();
      renderAt('&documentId=96');
      const user = userEvent.setup();

      await waitFor(() =>
        expect(requests.some((url) => url.pathname === '/api/v1/audit/cells/columns' && url.searchParams.get('documentId') === '96')).toBe(true),
      );
      const asked = requests.find((url) => url.pathname === '/api/v1/audit/cells/columns');
      expect(asked?.searchParams.has('from')).toBe(true);
      expect(asked?.searchParams.has('to')).toBe(true);

      await user.click(await screen.findByRole('textbox', { name: '⟦audit.columnDefId⟧' }));
      expect(await screen.findByRole('option', { name: 'Fuel / Steam / November (M11)' })).toBeTruthy();
      // Колонки зі сторінок журналу лишаються варіантами.
      expect(screen.getByRole('option', { name: 'Fuel / Gas / October (M10)' })).toBeTruthy();
      await user.click(screen.getByRole('option', { name: 'Fuel / Steam / November (M11)' }));
      await waitFor(() => expect(location).toContain('columnDefId=73999'));
    },
    SlowEnvTimeout,
  );

  it(
    'AC-8: без документа перелік колонок не питається',
    async () => {
      serve();
      renderAt('');
      await waitFor(() => expect(cellRequests().length).toBeGreaterThan(0));
      expect(requests.some((url) => url.pathname === '/api/v1/audit/cells/columns')).toBe(false);
    },
    SlowEnvTimeout,
  );

  it(
    'AC-5: однакові імена в «By user» розрізнені',
    async () => {
      serve();
      renderAt('');
      const user = userEvent.setup();

      await user.click(await screen.findByRole('textbox', { name: '⟦audit.author⟧' }));
      expect(await screen.findByRole('option', { name: `${TempName} (#5)` })).toBeTruthy();
      expect(screen.getByRole('option', { name: `${TempName} (#6)` })).toBeTruthy();
    },
    SlowEnvTimeout,
  );
});
