import type { JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { CellNavigationRequest } from '@/features/grid/cellNavigation';
import {
  completeCellNavigation,
  currentCellNavigation,
} from '@/features/grid/cellNavigation';
import { DocumentPage } from '@/pages/DocumentPage';

/**
 * `ФВ-5.6`: клік по зауваженню перевірки передає адресу сітці ТІЄЇ таблиці,
 * якої воно стосується, — через справжні інспектор (`UI-25`, вкладка Issues), `import()` модуля
 * переходу і `SheetTables`.
 *
 * ⚠ Обидві таблиці — на одному аркуші, і це вимушено: перемикання аркуша в
 * jsdom зависає синхронно ще ДО цієї зміни (відтворено на чистому
 * `origin/dev/integration` кліком по вкладці — `fireEvent.click` не
 * повертається). Перемикання аркуша за зауваженням — один рядок
 * `setSheet(target.sheetCode)` у `DocumentPage`; сам вибір аркуша тримає
 * `SheetTables.navigation.test.tsx` («запит чекає нового аркуша»).
 *
 * ⚠ `DocumentGrid` підмінений (тягне RevoGrid і власний зріз); `SheetTables` —
 * справжній, бо саме він вирішує, якій сітці віддати запит.
 */
const gridNavigation = new Map<number, CellNavigationRequest | null | undefined>();

vi.mock('@/features/grid/DocumentGrid', () => ({
  DocumentGrid: (props: {
    tableInstanceId: number;
    navigateTo?: CellNavigationRequest | null;
  }): JSX.Element => {
    gridNavigation.set(props.tableInstanceId, props.navigateTo);

    return <div data-testid={`grid-${String(props.tableInstanceId)}`} />;
  },
}));

vi.mock('@/features/methodologies/CalculationResultsPanel', () => ({
  CalculationResultsPanel: (): JSX.Element => <div data-testid="calc-stub" />,
}));

const FindingText = 'Fuel volume exceeds the cap';

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

function table(sheetCode: string, ordinal: number, tableDefId: number, tableInstanceId: number) {
  return {
    allowsDynamicRows: false,
    maxDynamicRows: null,
    sheetCode,
    sheetDefId: ordinal + 1,
    sheetNameL10n: { values: { en: sheetCode } },
    sheetOrdinal: ordinal,
    tableCode: `T${String(tableDefId)}`,
    tableDefId,
    tableInstanceId,
    tableNameL10n: { values: { en: `Table ${String(tableDefId)}` } },
    tableOrdinal: 0,
  };
}

function mockFetch(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('/api/v1/me')) {
        return jsonResponse({
          denies: [],
          grants: {},
          isSimulation: false,
          language: 'en',
          mustChangePassword: false,
          permissions: [],
          simulatedForUserId: null,
          userId: 1,
          userName: 'tester',
        });
      }

      if (url.includes('/validation')) {
        return jsonResponse({
          documentId: 1,
          periodKey: 202401,
          validated: true,
          messages: [
            {
              severity: 'Error',
              ruleCode: 'BALANCE',
              tableDefId: 20,
              rowKey: 'R1',
              columnCode: 'C1',
              message: FindingText,
              blocksSave: true,
            },
          ],
        });
      }

      if (url.includes('/tables')) {
        return jsonResponse([table('GEN', 0, 10, 100), table('GEN', 0, 20, 200)]);
      }

      if (url.includes('/api/v1/documents/1')) {
        return jsonResponse({
          businessKey: 'DOC-0001',
          createdAt: '2026-01-01T00:00:00Z',
          id: 1,
          nameL10n: { values: {} },
          projectId: 1,
          sheetCount: 1,
          sheetStates: { GEN: 'Draft' },
        });
      }

      return jsonResponse(null);
    }),
  );
}

afterEach(() => {
  const pending = currentCellNavigation();
  if (pending !== null) completeCellNavigation(pending.seq);
  gridNavigation.clear();
  vi.unstubAllGlobals();
});

describe('DocumentPage: перехід від зауваження до комірки (ФВ-5.6)', () => {
  it('клік по зауваженню віддає адресу сітці його таблиці — і лише їй', async () => {
    mockFetch();
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

    render(
      <MantineProvider>
        <MemoryRouter initialEntries={['/documents/1?periodKey=202401&panel=issues']}>
          <QueryClientProvider client={client}>
            <Routes>
              <Route path="/documents/:id" element={<DocumentPage />} />
            </Routes>
          </QueryClientProvider>
        </MemoryRouter>
      </MantineProvider>,
    );

    const finding = await screen.findByRole('button', { name: new RegExp(FindingText) });

    // Сітки ще не змонтовані: спостерігач у jsdom інертний (`src/test/setup.ts`).
    expect(screen.queryByTestId('grid-200')).toBeNull();

    fireEvent.click(finding);

    // ⛔ Адреса доїхала до сітки ПОТРІБНОЇ таблиці — з рядком і колонкою, — а
    // сусідня таблиця аркуша не змонтована заради чужого зауваження.
    await waitFor(() =>
      expect(gridNavigation.get(200)).toMatchObject({ tableDefId: 20, rowKey: 'R1', columnCode: 'C1' }),
    );
    expect(screen.queryByTestId('grid-100')).toBeNull();
    // ✎ `UI-22`: на екрані одна таблиця — перехід робить вибраною таблицю зауваження, а перша
    // таблиця аркуша (її сітка не змонтована) зникає з екрана, а не лишається стосом.
    await waitFor(() =>
      expect(document.querySelector('[data-table-slot="200"]')?.getAttribute('data-table-selected')).toBe('true'),
    );
    expect(document.querySelector('[data-table-slot="100"]')).toBeNull();
  });

  it('невиконаний запит скидається, коли сторінка розмонтована', async () => {
    mockFetch();
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

    const { unmount } = render(
      <MantineProvider>
        <MemoryRouter initialEntries={['/documents/1?periodKey=202401&panel=issues']}>
          <QueryClientProvider client={client}>
            <Routes>
              <Route path="/documents/:id" element={<DocumentPage />} />
            </Routes>
          </QueryClientProvider>
        </MemoryRouter>
      </MantineProvider>,
    );

    fireEvent.click(await screen.findByRole('button', { name: new RegExp(FindingText) }));
    await waitFor(() => expect(currentCellNavigation()).not.toBeNull());

    unmount();

    await waitFor(() => expect(currentCellNavigation()).toBeNull());
  });
});
