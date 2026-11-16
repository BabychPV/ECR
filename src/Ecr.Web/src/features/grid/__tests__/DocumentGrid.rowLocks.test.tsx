import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ColumnRegular } from '@revolist/revogrid';
import type { ColumnDto, TableSliceDto } from '@/api/types';
import type { RowLockDto } from '@/features/documents/rowLocks/api';
import { cancelAutosave } from '../autosave';
import { resetFocus } from '../focusStore';
import { resetPending } from '../pendingStore';
import { DocumentGrid } from '../DocumentGrid';

/**
 * ✎ B139 (F13, пункти c і d): блокування рядків справді підключені до сітки документа.
 *
 * c. Колонки, які отримує RevoGrid, пройшли `withRowLockMarks`: у першій колонці заблокованого рядка — значок
 *    (`data-row-lock-badge`, доступна назва з поясненням) і `data-row-lock` на комірці; в інших рядків — ні.
 * d. Кнопка «Заблоковані рядки: N» і перелік показують лише блокування ЦІЄЇ таблиці: блокування іншого аркуша
 *    (той самий код таблиці й ті самі ключі рядків), іншої таблиці та копії аркуша сюди не потрапляють.
 *
 * ⛔ Мутації (`DocumentGrid.tsx`): прибрати виклик `withRowLockMarks(marked, locksByRow, rowKeyOf)` або передати
 * в нього порожню мапу — червоніє тест c; прибрати `.filter((lock) => belongsTo(lock, lockTable))` у
 * `tableLocks` — червоніє тест d (лічильник 4 замість 1).
 *
 * ⚠ RevoGrid підмінено (як в усіх `DocumentGrid.*.test.tsx`): заглушка запам'ятовує передані колонки й рядки.
 */
let lastColumns: ColumnRegular[] = [];
let lastSource: Record<string, unknown>[] = [];

vi.mock('@revolist/react-datagrid', () => ({
  RevoGrid: (props: { columns?: ColumnRegular[]; source?: Record<string, unknown>[] }) => {
    lastColumns = props.columns ?? [];
    lastSource = props.source ?? [];

    return <div data-testid="revogrid-stub" />;
  },
}));

// ⚠ Перелік блокувань — `lazy()`-чанк: прогріти до тестів (перше відкриття під навантаженням не має вирішувати).
beforeAll(async () => {
  await import('@/features/rowLocks/RowLocksPanel');
}, 120_000);

function column(code: string, ordinal: number): ColumnDto {
  return {
    code,
    dataType: 'Text',
    defaultValue: null,
    displayFormat: null,
    header: `H-${code}`,
    id: ordinal + 1,
    isReadOnly: false,
    isRequired: false,
    isRequiredByMethodology: false,
    lookupRegistryDefId: null,
    ordinal,
    unitId: null,
    unitSymbol: null,
  } as unknown as ColumnDto;
}

function sliceFixture(): TableSliceDto {
  return {
    cellConfirmations: {},
    cellPermissions: {},
    periodKey: 202611,
    tableInstanceId: 1,
    columns: [column('C1', 0), column('C2', 1)],
    rows: ['r1', 'r2', 'r3'].map((rowKey, index) => ({
      cells: { C1: `${rowKey}-1`, C2: `${rowKey}-2` },
      isOrphaned: false,
      label: `Boiler ${rowKey}`,
      ordinal: index,
      rowKey,
      rowVersion: `v${String(index)}`,
      rowKind: 'Item' as const,
    })),
  } as unknown as TableSliceDto;
}

function lock(over: Partial<RowLockDto>): RowLockDto {
  return {
    canUnlock: false,
    id: 5,
    isActive: true,
    lockFrom: '2026-11-01',
    lockUntil: '2026-11-30',
    note: null,
    ownerDisplayName: 'A. Serikbayev',
    ownerUserId: 3,
    rowKey: 'r2',
    sheetCode: 'S1',
    sheetCopyId: null,
    tableCode: 'T1',
    ...over,
  };
}

/** Одне блокування цієї таблиці (рядок r2) і три чужі: інший аркуш, інша таблиця, копія аркуша. */
const Locks: RowLockDto[] = [
  lock({ id: 5, rowKey: 'r2' }),
  lock({ id: 6, rowKey: 'r1', sheetCode: 'S2' }),
  lock({ id: 7, rowKey: 'r3', tableCode: 'T9' }),
  lock({ id: 8, rowKey: 'r1', sheetCopyId: 4 }),
];

function show(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      const json = (body: unknown): Response =>
        new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });

      if (url.includes('/row-locks')) return json(Locks);
      if (url.includes('/me/preferences')) return json([]);
      if (url.includes('/api/v1/me')) {
        return json({
          denies: [],
          grants: {},
          isSimulation: false,
          language: 'en',
          mustChangePassword: false,
          permissions: ['Document.View'],
          simulatedForUserId: null,
          userId: 9,
          userName: 'tester',
        });
      }

      return json(sliceFixture());
    }),
  );

  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <MantineProvider>
      <QueryClientProvider client={client}>
        <DocumentGrid
          documentId={96}
          tableInstanceId={1}
          tableDefId={1}
          periodKey={202611}
          readOnly={false}
          allowsDynamicRows={false}
          maxDynamicRows={null}
          sheetCode="S1"
          tableCode="T1"
        />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

interface FakeNode {
  readonly tag: unknown;
  readonly props: Record<string, unknown> | null;
  readonly children: unknown[];
}

/** Підміна `h` RevoGrid: дерево звичайними об'єктами, щоб у ньому можна було шукати. */
const h = (tag: unknown, props: Record<string, unknown> | null, ...children: unknown[]): FakeNode => ({
  tag,
  props,
  children: children.flat(Infinity),
});

function findBadge(node: unknown): FakeNode | null {
  if (node === null || typeof node !== 'object') return null;
  const candidate = node as FakeNode;
  if (candidate.props !== null && candidate.props !== undefined && candidate.props['data-row-lock-badge'] !== undefined) {
    return candidate;
  }
  for (const child of candidate.children ?? []) {
    const found = findBadge(child);
    if (found !== null) return found;
  }

  return null;
}

/** Параметри комірки першої колонки для рядка — як їх дав би RevoGrid. */
function firstCell(rowKey: string): { column: ColumnRegular; props: Record<string, unknown> } {
  const column = lastColumns[0];
  const rowIndex = lastSource.findIndex((row) => row['__rowKey'] === rowKey);
  const model = lastSource[rowIndex];
  if (column === undefined || model === undefined) throw new Error(`немає першої колонки або рядка ${rowKey}`);

  return {
    column,
    props: { model, prop: column.prop, column, rowIndex, colIndex: 0, colType: 'rgCol', type: 'rgRow', value: model[String(column.prop)] },
  };
}

function badgeOf(rowKey: string): FakeNode | null {
  const { column, props } = firstCell(rowKey);

  return column.cellTemplate === undefined ? null : findBadge(column.cellTemplate(h as never, props as never));
}

function cellAttributes(rowKey: string): Record<string, unknown> {
  const { column, props } = firstCell(rowKey);

  return (column.cellProperties?.(props as never) ?? {}) as Record<string, unknown>;
}

afterEach(() => {
  cancelAutosave();
  resetPending();
  resetFocus();
  lastColumns = [];
  lastSource = [];
  localStorage.clear();
  vi.unstubAllGlobals();
});

describe('DocumentGrid: блокування рядків (B139, F13 c, d)', () => {
  it('c: перша колонка заблокованого рядка несе значок із поясненням і data-row-lock; інші рядки — ні', async () => {
    show();
    await screen.findByTestId('revogrid-stub');

    await waitFor(() => expect(lastSource.length).toBe(3));
    await waitFor(() => expect(badgeOf('r2'), 'значок рядка r2').not.toBeNull());

    const badge = badgeOf('r2');
    expect(badge?.props?.['data-row-lock-badge']).toBe('5');
    expect(badge?.props?.['role']).toBe('img');
    expect(String(badge?.props?.['aria-label'])).toContain('rowLock.badgeHint');

    const attributes = cellAttributes('r2');
    expect(attributes['data-row-lock']).toBe('5');
    expect(String(attributes['title'])).toContain('rowLock.badgeHint');

    // Рядки без блокування ЦІЄЇ таблиці (r1 заблоковано на іншому аркуші й у копії, r3 — в іншій таблиці).
    for (const rowKey of ['r1', 'r3']) {
      expect(badgeOf(rowKey), `значок рядка ${rowKey}`).toBeNull();
      expect(cellAttributes(rowKey)['data-row-lock'], `data-row-lock рядка ${rowKey}`).toBeUndefined();
    }
  });

  it('d: «Заблоковані рядки: N» і перелік — лише блокування цієї таблиці', async () => {
    show();
    await screen.findByTestId('revogrid-stub');

    const open = await waitFor(() => {
      const button = document.querySelector<HTMLElement>('[data-row-locks-open]');
      if (button === null) throw new Error('кнопки переліку блокувань немає');

      return button;
    });
    expect(open.getAttribute('data-row-locks-count')).toBe('1');

    fireEvent.click(open);
    const panel = await screen.findByRole('dialog');
    const listed = Array.from(panel.querySelectorAll('[data-row-lock-item]')).map((row) => row.getAttribute('data-row-lock-item'));
    expect(listed).toEqual(['5']);
  });
});
