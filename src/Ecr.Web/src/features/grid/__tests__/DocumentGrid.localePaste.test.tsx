import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ColumnDto, TableSliceDto } from '@/api/types';
import { DefaultLanguage, setLanguage } from '@/shared/i18n';
import { cancelAutosave } from '../autosave';
import { resetPending } from '../pendingStore';
import { DocumentGrid } from '../DocumentGrid';

/**
 * U1: `DocumentGrid` передає в `planPaste` предикат числових колонок
 * (`Decimal`/`Int`). Неоднозначне `1,234` в англійському інтерфейсі
 * відхиляється лише там; у текстовій колонці це текст і вставляється як є.
 *
 * Мутація (перевірено вручну, 2026-09-28): предикат `() => true` для всіх
 * колонок — червоніє «текстова колонка вставляє `1,234` як текст».
 */
vi.mock('@revolist/react-datagrid', () => ({
  RevoGrid: () => <div data-testid="revogrid-stub" />,
}));

function column(code: string, dataType: string, ordinal: number): ColumnDto {
  return {
    code,
    dataType,
    defaultValue: null,
    displayFormat: null,
    header: code,
    id: ordinal + 1,
    isReadOnly: false,
    isRequired: false,
    isRequiredByMethodology: false,
    lookupRegistryDefId: null,
    ordinal,
    unitId: null,
    unitSymbol: null,
  };
}

function sliceOf(columns: ColumnDto[]): TableSliceDto {
  return {
    cellConfirmations: {},
    cellPermissions: {},
    periodKey: 202609,
    tableInstanceId: 1,
    columns,
    rows: [
      {
        cells: Object.fromEntries(columns.map((c) => [c.code, null])),
        isOrphaned: false,
        label: null,
        ordinal: 0,
        rowKey: 'r1',
        rowVersion: 'v0',
        rowKind: 'Item' as const,
      },
    ],
  };
}

const patched: { rowKey: string; cells: { columnCode: string; value: unknown }[] }[] = [];

function mockServer(slice: TableSliceDto): void {
  patched.length = 0;

  vi.stubGlobal(
    'fetch',
    vi.fn(async (_path: string, init?: RequestInit) => {
      if (init?.method === 'PATCH') {
        const body = JSON.parse(String(init.body)) as {
          rows: { rowKey: string; cells: { columnCode: string; value: unknown }[] }[];
        };
        patched.push(...body.rows);

        return new Response(JSON.stringify({ appliedCells: 1, rowVersions: {}, validation: [] }), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }

      return new Response(JSON.stringify(slice), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      });
    }),
  );
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider>
      <QueryClientProvider client={client}>
        <DocumentGrid
          documentId={1}
          tableInstanceId={1}
          tableDefId={1}
          periodKey={202609}
          readOnly={false}
          allowsDynamicRows={false}
          maxDynamicRows={null}
        />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

function clipboard(text: string): { clipboardData: { getData: () => string; setData: ReturnType<typeof vi.fn> } } {
  return { clipboardData: { getData: () => text, setData: vi.fn() } };
}

beforeEach(() => {
  setLanguage('en');
});

afterEach(() => {
  cancelAutosave();
  resetPending();
  vi.unstubAllGlobals();
  setLanguage(DefaultLanguage);
});

describe('DocumentGrid (en): неоднозначне число при вставці', () => {
  it('текстова колонка вставляє `1,234` як текст', async () => {
    mockServer(sliceOf([column('T1', 'Text', 0)]));
    show();

    const stub = await screen.findByTestId('revogrid-stub');
    fireEvent.paste(stub, clipboard('1,234\n'));

    await waitFor(() => expect(patched).toHaveLength(1));

    expect(patched[0]?.cells.map((cell) => [cell.columnCode, cell.value])).toEqual([['T1', '1,234']]);
  });

  it('Decimal-колонка відхиляє `1,234` з поясненням; нічого не надіслано', async () => {
    mockServer(sliceOf([column('C1', 'Decimal', 0)]));
    show();

    const stub = await screen.findByTestId('revogrid-stub');
    fireEvent.paste(stub, clipboard('1,234\n'));

    const reason = await screen.findByText(/grid\.pasteAmbiguousNumber/);
    expect(reason.textContent).toContain('C1');
    expect(patched).toEqual([]);
  });
});
