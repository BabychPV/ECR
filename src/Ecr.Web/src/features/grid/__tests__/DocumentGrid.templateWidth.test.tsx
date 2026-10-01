import type { JSX } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { TableSliceDto } from '@/api/types';
import { cancelAutosave, useDocumentPending } from '../autosave';
import { resetPending } from '../pendingStore';
import { DocumentGrid } from '../DocumentGrid';
import { columnWidth } from '../columnWidths';

/**
 * `D-234`, `ФВ-2.7`: типова ширина колонки `ColumnDef.WidthPx` задана автором
 * шаблону і є початковою; ширина користувача (`D-201`) її перекриває;
 * «Скинути ширину» прибирає лише користувацькі.
 *
 * Мутаційні докази (перевірено вручну):
 * - `columnWidth(widths[code], column.widthPx)` → `widths[code] ?? DefaultColumnWidth`
 *   (ігнорувати `widthPx`) → червоні перший і третій тести;
 * - `columnWidth` без пріоритету користувацької (`templateWidth ?? userWidth`) → червоний другий;
 * - `reset` без `deletePreference` → червоний третій (DELETE не надійшов).
 */

vi.mock('@revolist/react-datagrid', () => ({
  RevoGrid: (props: { columns?: { prop?: unknown; size?: unknown }[] }) => (
    <div data-testid="revogrid-stub">
      {(props.columns ?? []).map((column) => (
        <span key={String(column.prop)} data-testid={`width-${String(column.prop)}`}>
          {String(column.size)}
        </span>
      ))}
    </div>
  ),
}));

const TableDefId = 88;

const baseColumn = {
  dataType: 'Int',
  defaultValue: null,
  displayFormat: null,
  isReadOnly: false,
  isRequired: false,
  isRequiredByMethodology: false,
  lookupRegistryDefId: null,
  unitId: null,
  unitSymbol: null,
} as const;

const slice: TableSliceDto = {
  cellConfirmations: {},
  cellPermissions: {},
  periodKey: 202609,
  tableInstanceId: 1,
  columns: [
    { ...baseColumn, code: 'C1', header: 'C1', id: 1, ordinal: 0, widthPx: 260 },
    { ...baseColumn, code: 'C2', header: 'C2', id: 2, ordinal: 1, widthPx: null },
  ],
  rows: [
    { cells: { C1: 1, C2: 2 }, isOrphaned: false, label: null, ordinal: 0, rowKey: 'r1', rowKind: 'Item', rowVersion: 'v1' },
  ],
};

function json(body: unknown): Response {
  return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
}

const deletes: string[] = [];

function mockServer(userWidths: Record<string, number> | null): void {
  deletes.length = 0;

  vi.stubGlobal(
    'fetch',
    vi.fn((input: string, init?: RequestInit) => {
      const url = String(input);

      if (url.includes('/api/v1/me/preferences')) {
        if (init?.method === 'DELETE') {
          deletes.push(decodeURIComponent(url));
          return Promise.resolve(new Response(null, { status: 204 }));
        }

        return Promise.resolve(
          json(
            userWidths === null
              ? []
              : [{ key: `grid.columnWidths.${String(TableDefId)}`, value: userWidths, updatedAt: '2026-10-01T00:00:00Z' }],
          ),
        );
      }

      return Promise.resolve(json(slice));
    }),
  );
}

function Host(): JSX.Element {
  useDocumentPending(1);

  return (
    <DocumentGrid
      documentId={1}
      tableInstanceId={1}
      tableDefId={TableDefId}
      periodKey={202609}
      readOnly={false}
      allowsDynamicRows={false}
      maxDynamicRows={null}
    />
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

const widthOf = (code: string): string | null => screen.getByTestId(`width-${code}`).textContent;

beforeEach(() => {
  localStorage.clear();
});

afterEach(() => {
  cancelAutosave();
  resetPending();
  vi.unstubAllGlobals();
  localStorage.clear();
});

describe('D-234: ширина колонки з шаблону', () => {
  it('columnWidth: користувацька → шаблонна → типова', () => {
    expect(columnWidth(300, 260)).toBe(300);
    expect(columnWidth(undefined, 260)).toBe(260);
    expect(columnWidth(undefined, null)).toBe(140);
    expect(columnWidth(undefined, undefined)).toBe(140);
  });

  it('початкова ширина — WidthPx шаблону; колонка без неї — типова', async () => {
    mockServer(null);
    renderHost();

    await screen.findByTestId('width-C1');
    expect(widthOf('C1')).toBe('260');
    expect(widthOf('C2')).toBe('140');
  });

  it('ширина користувача перекриває шаблонну', async () => {
    mockServer({ C1: 410 });
    renderHost();

    await vi.waitFor(() => {
      expect(widthOf('C1')).toBe('410');
    });
    expect(widthOf('C2')).toBe('140');
  });

  it('«Скинути ширину» повертає шаблонну і видаляє налаштування користувача', async () => {
    mockServer({ C1: 410 });
    renderHost();

    await vi.waitFor(() => {
      expect(widthOf('C1')).toBe('410');
    });

    fireEvent.click(await screen.findByRole('button', { name: /grid\.columnWidths\.reset/ }));
    await act(async () => {
      await Promise.resolve();
    });

    await vi.waitFor(() => {
      expect(widthOf('C1')).toBe('260');
    });
    expect(deletes).toEqual([expect.stringContaining(`grid.columnWidths.${String(TableDefId)}`)]);
    expect(screen.queryByRole('button', { name: /grid\.columnWidths\.reset/ })).toBeNull();
  });
});
