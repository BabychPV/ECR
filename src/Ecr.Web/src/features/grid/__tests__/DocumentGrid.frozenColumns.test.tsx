import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ColumnRegular } from '@revolist/revogrid';
import type { ColumnDto, TableSliceDto } from '@/api/types';
import { cancelAutosave } from '../autosave';
import { resetFocus } from '../focusStore';
import { resetPending } from '../pendingStore';
import { DocumentGrid } from '../DocumentGrid';
import type { CellNavigationRequest } from '../cellNavigation';
import { frozenColumnsCacheKey } from '@/features/preferences/frozenColumnsSync';
import { MeQueryKey } from '@/shared/session/useSession';

/** Поточний користувач (власник кешу `localStorage`, P3 05.11). */
const Me = 11;

/**
 * Закріплені ключові колонки (`frozenColumns.ts`): сітка отримує `pin: 'colPinStart'`, а події RevoGrid з
 * закріпленого й прокручуваного в'юпортів перетворюються на ГЛОБАЛЬНІ індекси колонок для вставки/копії.
 *
 * ⚠ RevoGrid підмінено (як в усіх `DocumentGrid.*.test.tsx`): заглушка віддає колонки, які їй передали, і
 * приймає справжні `CustomEvent('focuscell'|'setrange')` із `bubbles/composed`, як `revogr-overlay-selection`.
 */
let stub: HTMLElement | null = null;
let lastColumns: ColumnRegular[] = [];
const revo = {
  componentOnReady: vi.fn(() => Promise.resolve()),
  scrollToCoordinate: vi.fn(() => Promise.resolve()),
  setCellsFocus: vi.fn(() => Promise.resolve()),
};

vi.mock('@revolist/react-datagrid', () => ({
  RevoGrid: (props: { columns?: ColumnRegular[] }) => {
    lastColumns = props.columns ?? [];

    return (
      <div
        data-testid="revogrid-stub"
        ref={(node) => {
          if (node === null) return;
          stub = node;
          // Для «переходу до комірки»: справжні методи `revo-grid`, які викликає `revealGridCell`.
          if (node.querySelector('revo-grid') === null) node.append(Object.assign(document.createElement('revo-grid'), revo));
        }}
      />
    );
  },
}));

function column(code: string, ordinal: number, dataType: string): ColumnDto {
  return {
    code,
    dataType,
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

/** Підпис рядка + дві нечислові колонки + числова + нечислова: типово закріплюються підпис, C1, C2. */
function sliceFixture(): TableSliceDto {
  return {
    cellConfirmations: {},
    cellPermissions: {},
    periodKey: 202609,
    tableInstanceId: 1,
    columns: [column('C1', 0, 'Text'), column('C2', 1, 'Text'), column('C3', 2, 'Decimal'), column('C4', 3, 'Text')],
    rows: ['r1', 'r2', 'r3'].map((rowKey, index) => ({
      cells: { C1: `${rowKey}-1`, C2: `${rowKey}-2`, C3: '5', C4: `${rowKey}-4` },
      isOrphaned: false,
      label: `Рядок ${rowKey}`,
      ordinal: index,
      rowKey,
      rowVersion: `v${index}`,
      rowKind: 'Item' as const,
    })),
  } as unknown as TableSliceDto;
}

const patched: { rowKey: string; cells: { columnCode: string; value: unknown }[] }[] = [];
const preferenceWrites: { method: string; path: string; body: unknown }[] = [];

/** Збережений вибір користувача (ключ grid.frozenColumns.1); без нього таблиця (5 колонок) вузька й типово не закріплюється. */
function mockServer(frozen?: number): void {
  patched.length = 0;
  preferenceWrites.length = 0;

  vi.stubGlobal(
    'fetch',
    vi.fn(async (path: string, init?: RequestInit) => {
      if (init?.method === 'PATCH') {
        const body = JSON.parse(String(init.body)) as { rows: typeof patched };
        patched.push(...body.rows);

        return new Response(JSON.stringify({ appliedCells: 1, rowVersions: {}, validation: [] }), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }

      if (String(path).includes('/me/preferences')) {
        if (init?.method === 'PUT' || init?.method === 'DELETE') {
          preferenceWrites.push({ method: init.method, path: String(path), body: init.body === undefined ? null : JSON.parse(String(init.body)) });
        }

        const isRead = init?.method === 'GET' || init?.method === undefined;
        const saved = frozen === undefined ? [] : [{ key: 'grid.frozenColumns.1', value: frozen, updatedAt: '2026-11-05T00:00:00Z' }];

        return new Response(isRead ? JSON.stringify(saved) : null, {
          status: init?.method === 'DELETE' ? 204 : 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }

      return new Response(JSON.stringify(sliceFixture()), { status: 200, headers: { 'Content-Type': 'application/json' } });
    }),
  );
}

function show(navigateTo?: CellNavigationRequest): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  client.setQueryData(MeQueryKey, { userId: Me });

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
          {...(navigateTo === undefined ? {} : { navigateTo })}
        />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

/** `focuscell` з указаного в'юпорту: `x` - від початку В'ЮПОРТУ, як у справжнього RevoGrid. */
function focus(colType: 'colPinStart' | 'rgCol', x: number, y: number): void {
  stub?.dispatchEvent(
    new CustomEvent('focuscell', {
      bubbles: true,
      composed: true,
      detail: { rowType: 'rgRow', colType, focus: { x, y }, end: { x, y } },
    }),
  );
}

/** `setrange` з указаного в'юпорту (шлях події містить `revogr-viewport-scroll.<colType>`). */
function range(colType: 'colPinStart' | 'rgCol', x: number, y: number, x1: number, y1: number): void {
  const viewport = document.createElement('revogr-viewport-scroll');
  viewport.classList.add(colType);
  stub?.append(viewport);
  viewport.dispatchEvent(
    new CustomEvent('setrange', { bubbles: true, composed: true, detail: { type: 'rgRow', x, y, x1, y1 } }),
  );
  viewport.remove();
}

function clipboard(text: string): { clipboardData: { getData: () => string; setData: ReturnType<typeof vi.fn> } } {
  return { clipboardData: { getData: () => text, setData: vi.fn() } };
}

function pinnedProps(): (string | undefined)[] {
  return lastColumns.map((c) => c.pin);
}

afterEach(() => {
  cancelAutosave();
  resetPending();
  resetFocus();
  stub = null;
  lastColumns = [];
  localStorage.clear();
  revo.componentOnReady.mockClear();
  revo.scrollToCoordinate.mockClear();
  revo.setCellsFocus.mockClear();
  vi.unstubAllGlobals();
});

describe('DocumentGrid: закріплені колонки', () => {
  it('вузька таблиця: типово нічого не закріплено; явний вибір «до колонки C2» закріплює підпис, C1, C2', async () => {
    mockServer();
    show();
    await screen.findByTestId('revogrid-stub');

    await waitFor(() => expect(lastColumns).toHaveLength(5));
    expect(pinnedProps()).toEqual([undefined, undefined, undefined, undefined, undefined]);
    expect(lastColumns.map((c) => c.prop)).toEqual(['__rowLabel', 'C1', 'C2', 'C3', 'C4']);

    const select = await screen.findByRole('combobox', { name: /grid\.frozenColumns\.label/ });
    fireEvent.change(select, { target: { value: '3' } });

    await waitFor(() => expect(pinnedProps()).toEqual(['colPinStart', 'colPinStart', 'colPinStart', undefined, undefined]));
  });

  it('вставка з закріпленої колонки лягає у правильні колонки через межу в\'юпортів', async () => {
    mockServer(3);
    show();
    const grid = await screen.findByTestId('revogrid-stub');
    await waitFor(() => expect(lastColumns).toHaveLength(5));
    await waitFor(() => expect(pinnedProps()[2]).toBe('colPinStart'));

    // Закріплений в'юпорт: x=2 - це C2 (0 - підпис, 1 - C1).
    focus('colPinStart', 2, 1);
    fireEvent.paste(grid, clipboard('ab\t7\n'));

    await waitFor(() => expect(patched).toHaveLength(1));
    expect(patched[0]?.rowKey).toBe('r2');
    expect(patched[0]?.cells.map((cell) => cell.columnCode)).toEqual(['C2', 'C3']);
  });

  it('фокус у прокручуваному в\'юпорті зсувається на кількість закріплених: rgCol x=0 - це C3', async () => {
    mockServer(3);
    show();
    const grid = await screen.findByTestId('revogrid-stub');
    await waitFor(() => expect(lastColumns).toHaveLength(5));
    await waitFor(() => expect(pinnedProps()[2]).toBe('colPinStart'));

    focus('rgCol', 0, 0);
    fireEvent.paste(grid, clipboard('9\tz\n'));

    await waitFor(() => expect(patched).toHaveLength(1));
    expect(patched[0]?.rowKey).toBe('r1');
    expect(patched[0]?.cells.map((cell) => cell.columnCode)).toEqual(['C3', 'C4']);
  });

  it('копіювання діапазону всередині кожного боку віддає свої колонки', async () => {
    mockServer(3);
    show();
    const grid = await screen.findByTestId('revogrid-stub');
    await waitFor(() => expect(lastColumns).toHaveLength(5));
    await waitFor(() => expect(pinnedProps()[2]).toBe('colPinStart'));

    range('colPinStart', 1, 0, 2, 1);
    const pinnedCopy = clipboard('');
    fireEvent.copy(grid, pinnedCopy);
    expect(pinnedCopy.clipboardData.setData).toHaveBeenCalledWith('text/plain', 'r1-1\tr1-2\nr2-1\tr2-2\n');

    range('rgCol', 1, 0, 1, 1);
    const scrolledCopy = clipboard('');
    fireEvent.copy(grid, scrolledCopy);
    expect(scrolledCopy.clipboardData.setData).toHaveBeenCalledWith('text/plain', 'r1-4\nr2-4\n');
  });

  it('вибір «None» знімає закріплення, пишеться в кеш і на сервер; події рахуються без зсуву', async () => {
    mockServer(3);
    show();
    const grid = await screen.findByTestId('revogrid-stub');
    await waitFor(() => expect(lastColumns).toHaveLength(5));
    await waitFor(() => expect(pinnedProps()[2]).toBe('colPinStart'));

    const select = await screen.findByRole('combobox', { name: /grid\.frozenColumns\.label/ });
    fireEvent.change(select, { target: { value: '0' } });

    await waitFor(() => expect(pinnedProps()).toEqual([undefined, undefined, undefined, undefined, undefined]));
    expect(localStorage.getItem(frozenColumnsCacheKey(Me, 1))).toBe('0');
    await waitFor(() => expect(preferenceWrites).toHaveLength(1));
    expect(preferenceWrites[0]).toMatchObject({ method: 'PUT', body: 0 });
    expect(preferenceWrites[0]?.path).toContain('grid.frozenColumns.1');

    // Без закріплених rgCol x=1 - це глобальна колонка 1 = C1.
    focus('rgCol', 1, 0);
    fireEvent.paste(grid, clipboard('p\tq\n'));

    await waitFor(() => expect(patched).toHaveLength(1));
    expect(patched[0]?.cells.map((cell) => cell.columnCode)).toEqual(['C1', 'C2']);
  });

  it('вибір «до колонки C3» закріплює чотири перші колонки; «Default» повертає типове й видаляє ключ', async () => {
    mockServer(3);
    show();
    await screen.findByTestId('revogrid-stub');
    await waitFor(() => expect(lastColumns).toHaveLength(5));
    await waitFor(() => expect(pinnedProps()[2]).toBe('colPinStart'));

    const select = await screen.findByRole('combobox', { name: /grid\.frozenColumns\.label/ });
    fireEvent.change(select, { target: { value: '4' } });
    await waitFor(() => expect(pinnedProps()).toEqual(['colPinStart', 'colPinStart', 'colPinStart', 'colPinStart', undefined]));

    fireEvent.change(select, { target: { value: 'default' } });
    await waitFor(() => expect(pinnedProps()).toEqual([undefined, undefined, undefined, undefined, undefined]));
    expect(localStorage.getItem(frozenColumnsCacheKey(Me, 1))).toBeNull();
    await waitFor(() => expect(preferenceWrites.map((w) => w.method)).toEqual(['PUT', 'DELETE']));
  });

  it('перехід до комірки: прокручувана ціль іде в rgCol зі зсувом, закріплена - в colPinStart', async () => {
    mockServer(3);
    show({ tableDefId: 1, rowKey: 'r2', columnCode: 'C3', seq: 1 });

    // C3 - глобальна колонка 3; 3 закріплені (підпис, C1, C2), тож це перша колонка rgCol: x=0.
    await waitFor(() => expect(revo.setCellsFocus).toHaveBeenCalledWith({ x: 0, y: 1 }, { x: 0, y: 1 }));
    expect(revo.scrollToCoordinate).toHaveBeenCalledWith({ x: 0, y: 1 });
  });

  it('перехід до закріпленої колонки: фокус у colPinStart зі своїм x, без прокрутки по x', async () => {
    mockServer(3);
    show({ tableDefId: 1, rowKey: 'r1', columnCode: 'C2', seq: 1 });

    // C2 - глобальна колонка 2, у закріпленому в'юпорті x=2.
    await waitFor(() => expect(revo.setCellsFocus).toHaveBeenCalledWith({ x: 2, y: 0 }, { x: 2, y: 0 }, 'colPinStart'));
    expect(revo.scrollToCoordinate).toHaveBeenCalledWith({ y: 0 });
  });
  it('випадний список має підпис і пункти: Default, None, «до колонки» для перших колонок', async () => {
    mockServer();
    show();
    await screen.findByTestId('revogrid-stub');

    const select = await screen.findByRole('combobox', { name: /grid\.frozenColumns\.label/ });
    const options = Array.from(select.querySelectorAll('option')).map((o) => o.value);

    // 5 колонок сітки: default, 0, 1..4 (остання колонка не пропонується)
    expect(options).toEqual(['default', '0', '1', '2', '3', '4']);
    expect((select as HTMLSelectElement).value).toBe('default');
  });
});
