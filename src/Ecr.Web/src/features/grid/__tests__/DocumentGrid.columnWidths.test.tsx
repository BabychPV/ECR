import type { JSX } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, fireEvent, render, screen, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { TableSliceDto } from '@/api/types';
import { cancelAutosave, useDocumentPending } from '../autosave';
import { resetPending } from '../pendingStore';
import { DocumentGrid } from '../DocumentGrid';

/**
 * `ФВ-14.29`, `D-201`: ширини колонок — на КОРИСТУВАЧА і ВИЗНАЧЕННЯ таблиці.
 *
 * ⛔ Ключ — `tableDefId`, а не `tableInstanceId`: екземпляр новий на кожен
 * документ × період, і ширини, прив'язані до нього, губилися б щомісяця.
 * Тому тест рендерить ДВА екземпляри (різні `tableInstanceId`) одного
 * визначення і вимагає однакових ширин.
 *
 * Мутаційні докази (перевірено вручну у власному worktree):
 * - `useColumnWidths(tableInstanceId)` замість `tableDefId` → червоні обидва
 *   тести (ширина 140 замість серверних 300);
 * - `onColumnResize` без виклику `saveColumnWidths` → червоний другий тест
 *   (ширина лишилась 300 замість 222, PUT немає).
 */

vi.mock('@revolist/react-datagrid', () => ({
  RevoGrid: (props: {
    columns?: { prop?: unknown; size?: unknown }[];
    onAftercolumnresize?: (event: { detail: unknown }) => void;
  }) => (
    <div data-testid="revogrid-stub">
      {(props.columns ?? []).map((column) => (
        <span key={String(column.prop)} data-testid={`width-${String(column.prop)}`}>
          {String(column.size)}
        </span>
      ))}
      <button
        type="button"
        onClick={() => props.onAftercolumnresize?.({ detail: { 0: { prop: 'C1', size: 222 } } })}
      >
        simulate-resize
      </button>
    </div>
  ),
}));

const TableDefId = 77;

function sliceFixture(tableInstanceId: number): TableSliceDto {
  return {
    cellConfirmations: {},
    cellPermissions: {},
    periodKey: 202609,
    tableInstanceId,
    columns: [
      {
        code: 'C1',
        dataType: 'Int',
        defaultValue: null,
        displayFormat: null,
        header: 'C1',
        id: 1,
        isReadOnly: false,
        isRequired: false,
        isRequiredByMethodology: false,
        lookupRegistryDefId: null,
        ordinal: 0,
        unitId: null,
        unitSymbol: null,
      },
    ],
    rows: [
      { cells: { C1: 1 }, isOrphaned: false, label: null, ordinal: 0, rowKey: 'r1', rowKind: 'Item', rowVersion: 'v1' },
    ],
  };
}

function json(body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  });
}

const puts: { url: string; body: unknown }[] = [];

function mockServer(): void {
  puts.length = 0;

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: string, init?: RequestInit) => {
      const url = String(input);

      if (url.includes('/api/v1/me/preferences')) {
        if (init?.method === 'PUT') {
          const body = JSON.parse(String(init.body)) as unknown;
          puts.push({ url, body });
          return json({ key: 'x', value: body, updatedAt: '2026-09-28T00:00:00Z' });
        }

        // Серверні ширини — під ключем ВИЗНАЧЕННЯ таблиці.
        return json([
          {
            key: `grid.columnWidths.${String(TableDefId)}`,
            value: { C1: 300 },
            updatedAt: '2026-09-28T00:00:00Z',
          },
        ]);
      }

      const match = /\/tables\/(\d+)/.exec(url);
      return json(sliceFixture(Number(match?.[1] ?? 1)));
    }),
  );
}

function Host(): JSX.Element {
  useDocumentPending(1);

  return (
    <>
      {[1, 2].map((tableInstanceId) => (
        <div key={tableInstanceId} data-testid={`host-${String(tableInstanceId)}`}>
          <DocumentGrid
            documentId={1}
            tableInstanceId={tableInstanceId}
            tableDefId={TableDefId}
            periodKey={202609}
            readOnly={false}
            allowsDynamicRows={false}
            maxDynamicRows={null}
          />
        </div>
      ))}
    </>
  );
}

function renderHost(): void {
  render(
    <MantineProvider>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <Host />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

async function wait(ms: number): Promise<void> {
  await act(async () => {
    await new Promise((resolve) => setTimeout(resolve, ms));
  });
}

function widthIn(host: string): string | null {
  return within(screen.getByTestId(host)).getByTestId('width-C1').textContent;
}

beforeEach(() => {
  localStorage.clear();
  mockServer();
});

afterEach(() => {
  cancelAutosave();
  resetPending();
  vi.unstubAllGlobals();
  localStorage.clear();
});

describe('ФВ-14.29: ширини колонок на користувача і визначення таблиці (D-201)', () => {
  it('ФВ-14.29: сітка бере ширини з вподобань користувача за tableDefId — два екземпляри однієї таблиці мають однакові ширини', async () => {
    renderHost();

    await within(await screen.findByTestId('host-1')).findByTestId('width-C1');
    await within(screen.getByTestId('host-2')).findByTestId('width-C1');
    await vi.waitFor(() => {
      expect(widthIn('host-1')).toBe('300');
      expect(widthIn('host-2')).toBe('300');
    });
  });

  it('ФВ-14.29: зміна ширини пишеться на сервер під grid.columnWidths.{tableDefId} (з дебаунсом)', async () => {
    renderHost();

    await vi.waitFor(() => {
      expect(widthIn('host-1')).toBe('300');
    });

    fireEvent.click(
      within(screen.getByTestId('host-1')).getByRole('button', { name: 'simulate-resize' }),
    );

    // Кеш і стан — одразу; сервер — лише після дебаунсу.
    expect(widthIn('host-1')).toBe('222');
    expect(puts).toEqual([]);

    await wait(700);

    expect(puts).toEqual([
      {
        url: `/api/v1/me/preferences/${encodeURIComponent(`grid.columnWidths.${String(TableDefId)}`)}`,
        body: { C1: 222 },
      },
    ]);

    // Інший екземпляр того самого визначення бачить нові ширини з кешу запиту.
    await vi.waitFor(() => {
      expect(widthIn('host-2')).toBe('222');
    });
  });
});
