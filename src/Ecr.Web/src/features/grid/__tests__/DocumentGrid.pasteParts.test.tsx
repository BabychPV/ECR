import { type JSX } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ColumnDto, TableSliceDto } from '@/api/types';
import { cancelAutosave, useDocumentPending } from '../autosave';
import { pendingSlice, resetPending } from '../pendingStore';
import { PatchCellsMaxCells } from '../patchParts';
import { DocumentGrid } from '../DocumentGrid';

/**
 * PSS-17 крізь сітку: Ctrl+V на 51 000 комірок (1 020 рядків × 50 колонок) — два `PATCH` по черзі, кожен у межах
 * стелі сервера; друга частина йде з версіями рядків, які вже підняла відповідь першої (мок-сервер звіряє
 * `baseVersion` як справжній: чужа версія — `409`).
 *
 * Мутаційний доказ (не в коміті): у `DocumentGrid.save` замість `saveInParts(edits, savePart, …)` —
 * `savePart(edits)` (один запит) → червоні всі три тести (один PATCH на 51 000, `422` мок-сервера).
 */

vi.mock('@revolist/react-datagrid', () => ({
  RevoGrid: () => <div data-testid="revogrid-stub" />,
}));

vi.mock('@mantine/notifications', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@mantine/notifications')>();
  return { ...actual, notifications: { ...actual.notifications, show: vi.fn(), update: vi.fn(), hide: vi.fn() } };
});

const DocumentId = 9;
const Table = 1;
const Period = 202609;
const Rows = 1020;
const Columns = 50;

function column(code: string, ordinal: number): ColumnDto {
  return {
    code,
    dataType: 'Int',
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

const codes = Array.from({ length: Columns }, (_, c) => `C${String(c)}`);
const rowKeys = Array.from({ length: Rows }, (_, r) => `r${String(r)}`);

/** Стан «сервера»: значення і версії рядків. */
const server = {
  cells: new Map<string, number>(),
  versions: new Map<string, string>(),
};

/** Комірка без права запису (NoGrant); 
ull — усі відкриті. */
let deniedCell: string | null = null;

function sliceFixture(): TableSliceDto {
  return {
    cellConfirmations: {},
    cellPermissions: deniedCell === null ? {} : { [deniedCell]: 'NoGrant' },
    periodKey: Period,
    tableInstanceId: Table,
    columns: codes.map(column),
    rows: rowKeys.map((rowKey, ordinal) => ({
      cells: Object.fromEntries(codes.map((code) => [code, server.cells.get(`${rowKey}:${code}`) ?? 0])),
      isOrphaned: false,
      label: null,
      ordinal,
      rowKey,
      rowKind: 'Item',
      rowVersion: server.versions.get(rowKey) ?? 'v0',
    })),
  };
}

interface Patch {
  cells: number;
  status: number;
}

const patches: Patch[] = [];
/** Примусові відповіді наступним PATCH (`500` — збій). */
const forced: number[] = [];

function problem(status: number, errorCode: string): Response {
  return new Response(JSON.stringify({ title: 'x', status, detail: 'x', errorCode, correlationId: 'c' }), {
    status,
    headers: { 'Content-Type': 'application/problem+json' },
  });
}

function mockServer(): void {
  patches.length = 0;
  server.cells.clear();
  server.versions.clear();

  vi.stubGlobal(
    'fetch',
    vi.fn(async (_path: string, init?: RequestInit) => {
      if (init?.method !== 'PATCH') {
        return new Response(JSON.stringify(sliceFixture()), { status: 200, headers: { 'Content-Type': 'application/json' } });
      }

      const request = JSON.parse(String(init.body)) as {
        rows: { rowKey: string; baseVersion: string | null; cells: { columnCode: string; value: number }[] }[];
      };
      const cells = request.rows.reduce((sum, row) => sum + row.cells.length, 0);

      // ⛔ Як `PatchCellsRequest.EnsureWithinCellLimit`: понад стелю — відмова всього пакета.
      if (cells > PatchCellsMaxCells) {
        patches.push({ cells, status: 422 });
        return problem(422, 'ECR-REQ-0422');
      }

      const failure = forced.shift();
      if (failure !== undefined) {
        patches.push({ cells, status: failure });
        return problem(failure, 'ECR-SYS-0500');
      }

      // ⛔ Оптимістичне блокування, як у сховищі: версія не та — `409`, нічого не записано.
      const stale = request.rows.find((row) => row.baseVersion !== (server.versions.get(row.rowKey) ?? 'v0'));
      if (stale !== undefined) {
        patches.push({ cells, status: 409 });
        return problem(409, 'ECR-CELL-0409');
      }

      const rowVersions: Record<string, string> = {};
      for (const row of request.rows) {
        for (const cell of row.cells) server.cells.set(`${row.rowKey}:${cell.columnCode}`, Number(cell.value));
        const next = `v${String(Number((server.versions.get(row.rowKey) ?? 'v0').slice(1)) + 1)}`;
        server.versions.set(row.rowKey, next);
        rowVersions[row.rowKey] = next;
      }
      patches.push({ cells, status: 200 });

      return new Response(JSON.stringify({ appliedCells: cells, rowVersions, validation: [] }), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      });
    }),
  );
}

function DocumentHost(): JSX.Element {
  useDocumentPending(DocumentId);

  return (
    <DocumentGrid
      documentId={DocumentId}
      tableInstanceId={Table}
      tableDefId={1}
      periodKey={Period}
      readOnly={false}
      allowsDynamicRows={false}
      maxDynamicRows={null}
    />
  );
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider>
      <QueryClientProvider client={client}>
        <DocumentHost />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

/** Увесь аркуш значеннями `r * 50 + c + 1` (якір без виділення — кут таблиці). */
function pasteAll(): void {
  const text = rowKeys.map((_, r) => codes.map((_, c) => String(r * Columns + c + 1)).join('\t')).join('\n');

  fireEvent.paste(screen.getByTestId('revogrid-stub'), { clipboardData: { getData: () => text } });
}

function serverHoldsPaste(): boolean {
  return rowKeys.every((rowKey, r) => codes.every((code, c) => server.cells.get(`${rowKey}:${code}`) === r * Columns + c + 1));
}

function messages(): string[] {
  return [
    ...vi.mocked(notifications.show).mock.calls.map(([data]) => String(data.message)),
    ...vi.mocked(notifications.update).mock.calls.map(([data]) => String(data.message)),
  ];
}

beforeEach(() => {
  vi.mocked(notifications.show).mockClear();
  vi.mocked(notifications.update).mockClear();
  vi.mocked(notifications.hide).mockClear();
});

afterEach(() => {
  deniedCell = null;
  forced.length = 0;
  cancelAutosave();
  resetPending();
  vi.unstubAllGlobals();
});

describe('PSS-17: вставка понад стелю сервера — частинами', () => {
  it('51 000 комірок — два PATCH по черзі, кожен ≤ 50 000; усе збережено, хід і підсумок видно', async () => {
    mockServer();
    show();
    await screen.findByTestId('revogrid-stub');

    pasteAll();

    await waitFor(() => expect(pendingSlice(Table, Period).size).toBe(0), { timeout: 10_000 });

    expect(patches).toEqual([
      { cells: 50_000, status: 200 },
      { cells: 1_000, status: 200 },
    ]);
    expect(serverHoldsPaste()).toBe(true);
    expect(messages().some((message) => message.includes('grid.saveParts.progress'))).toBe(true);
    expect(messages().at(-1)).toContain('grid.saveParts.done');
  }, 30_000);

  it('збій другої частини: першу збережено, решта лишається незбереженою в сховищі, і людині сказано скільки', async () => {
    mockServer();
    show();
    await screen.findByTestId('revogrid-stub');

    const firstPass = patches.length;

    // Перша частина пройде, друга — `500` (збій мережі/сервера).
    let calls = 0;
    const fetchMock = vi.mocked(fetch);
    const real = fetchMock.getMockImplementation();
    fetchMock.mockImplementation(async (input, init) => {
      if (init?.method === 'PATCH') {
        calls += 1;
        if (calls === 2) forced.push(500);
      }
      return (real as NonNullable<typeof real>)(input, init);
    });

    pasteAll();

    await waitFor(() => expect(patches.length - firstPass).toBe(2), { timeout: 10_000 });
    await waitFor(() => expect(messages().some((message) => message.includes('grid.saveParts.partial'))).toBe(true));

    expect(patches.slice(firstPass)).toEqual([
      { cells: 50_000, status: 200 },
      { cells: 1_000, status: 500 },
    ]);

    // Збережене — збережене; незбережене — рівно друга частина, у сховищі, з тим самим значенням.
    const left = pendingSlice(Table, Period);
    expect(left.size).toBe(1_000);
    expect([...left.values()].every((edit) => Number(edit.rowKey.slice(1)) >= 1000)).toBe(true);
    expect(server.cells.get('r0:C0')).toBe(1);
    expect(server.cells.get('r1019:C49')).toBeUndefined();

    // Повтор («Retry save») довозить решту — тим самим шляхом, що й будь-яку відмову.
    fireEvent.click(await screen.findByRole('button', { name: /grid\.retrySave/ }));
    await waitFor(() => expect(pendingSlice(Table, Period).size).toBe(0), { timeout: 10_000 });
    expect(serverHoldsPaste()).toBe(true);
  }, 30_000);

  it('закрита комірка в ОСТАННІЙ частині відхиляє всю вставку ДО першого запиту — як і доти', async () => {
    deniedCell = 'r1019:C49';
    mockServer();
    show();
    await screen.findByTestId('revogrid-stub');

    pasteAll();
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 300));
    });

    expect(patches).toEqual([]);
    expect(pendingSlice(Table, Period).size).toBe(0);
    expect(messages()).toEqual([]);
  }, 30_000);

  it('Undo після часткового збереження повертає ВСЕ, що показано: і збережену частину, і незбережену', async () => {
    mockServer();
    show();
    const grid = await screen.findByTestId('revogrid-stub');

    let calls = 0;
    const fetchMock = vi.mocked(fetch);
    const real = fetchMock.getMockImplementation();
    fetchMock.mockImplementation(async (input, init) => {
      if (init?.method === 'PATCH') {
        calls += 1;
        if (calls === 2) forced.push(500);
      }
      return (real as NonNullable<typeof real>)(input, init);
    });

    pasteAll();
    await waitFor(() => expect(patches).toHaveLength(2), { timeout: 10_000 });
    await waitFor(() => expect(messages().some((message) => message.includes('grid.saveParts.partial'))).toBe(true));
    expect(pendingSlice(Table, Period).size).toBe(1_000);

    await act(async () => {
      fireEvent.keyDown(grid, { key: 'z', ctrlKey: true });
      await Promise.resolve();
    });

    await waitFor(() => expect(pendingSlice(Table, Period).size).toBe(0), { timeout: 10_000 });

    // Сервер — як до вставки: збережена частина повернута записом, незбережена й не доходила.
    expect(rowKeys.every((rowKey) => codes.every((code) => (server.cells.get(`${rowKey}:${code}`) ?? 0) === 0))).toBe(true);
    for (const patch of patches.slice(2)) {
      expect(patch.status).toBe(200);
      expect(patch.cells).toBeLessThanOrEqual(PatchCellsMaxCells);
    }
  }, 30_000);
});
