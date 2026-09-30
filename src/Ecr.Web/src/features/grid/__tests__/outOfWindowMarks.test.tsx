import { afterEach, describe, expect, it } from 'vitest';
import { act, renderHook, waitFor } from '@testing-library/react';
import { QueryClient } from '@tanstack/react-query';
import type {
  ColumnDto,
  PatchCellsRequest,
  PatchCellsResponse,
  TableSliceDto,
} from '@/api/types';
import { gridColumns } from '../DocumentGrid';
import { NoLocalFlags } from '../cellState';
import { applyPatchLocally } from '../useCellPatch';
import {
  mergeOutOfWindow,
  outOfWindowMarksOf,
  recordOutOfWindow,
  resetOutOfWindowMarks,
  useOutOfWindowMarks,
} from '../outOfWindowMarks';

/**
 * `ФВ-2.16`, `D-239`: значок «правка поза вікном» на комірці сітки.
 *
 * ⚠ Ланцюг доводиться від відповіді `PATCH` до атрибутів комірки: адреси з
 * `PatchCellsResponse.outOfWindow` → `outOfWindowMarks.ts` (через `import()` у
 * `applyPatchLocally`) → клас, атрибут і підказка в `gridColumns`.
 */

function column(overrides: Partial<ColumnDto> = {}): ColumnDto {
  return {
    id: 1,
    code: 'C1',
    header: 'Колонка 1',
    dataType: 'Decimal',
    ordinal: 1,
    isReadOnly: false,
    isRequired: false,
    isRequiredByMethodology: false,
    displayFormat: null,
    defaultValue: null,
    lookupRegistryDefId: null,
    unitId: null,
    unitSymbol: null,
    ...overrides,
  };
}

function row(rowKey: string, ordinal: number): TableSliceDto['rows'][number] {
  return {
    rowKey,
    ordinal,
    rowKind: 'Item',
    label: null,
    rowVersion: '0x01',
    cells: {},
    isOrphaned: false,
  };
}

function slice(): TableSliceDto {
  return {
    tableInstanceId: 7,
    periodKey: 202609,
    columns: [column()],
    rows: [row('R1', 1), row('R2', 2)],
    cellPermissions: {},
    cellConfirmations: {},
  };
}

function cellPropsFor(
  columns: ReturnType<typeof gridColumns>,
  rowKey: string,
): Record<string, unknown> {
  const found = columns.find((c) => c.prop === 'C1');
  if (found === undefined || typeof found.cellProperties !== 'function') {
    throw new Error('Немає колонки C1 або в неї немає cellProperties');
  }

  return found.cellProperties({
    model: { __rowKey: rowKey },
    prop: 'C1',
  } as never) as Record<string, unknown>;
}

const noRequiredInput = {
  blocked: new Map<string, string>(),
  warning: new Map<string, string>(),
};

function columnsWith(
  outOfWindow: ReadonlySet<string>,
): ReturnType<typeof gridColumns> {
  return gridColumns(
    slice(),
    false,
    NoLocalFlags,
    {},
    noRequiredInput,
    new Map(),
    new Map(),
    new Map(),
    new Set(),
    null,
    null,
    outOfWindow,
  );
}

const request: PatchCellsRequest = {
  origin: 'UserEdit',
  tableInstanceId: 7,
  periodKey: 202609,
  rows: [{ rowKey: 'R1', baseVersion: '0x01', cells: [{ columnCode: 'C1', isEmpty: false, value: '5' }] }],
};

function response(outOfWindow?: string[] | null): PatchCellsResponse {
  const base: PatchCellsResponse = { appliedCells: 1, rowVersions: { R1: '0x02' }, validation: [] };

  return outOfWindow === undefined ? base : { ...base, outOfWindow };
}

afterEach(() => {
  resetOutOfWindowMarks();
});

describe('gridColumns — значок правки поза вікном (ФВ-2.16)', () => {
  it('позначена комірка отримує клас, атрибут і підказку-причину', () => {
    const props = cellPropsFor(columnsWith(new Set(['R1:C1'])), 'R1');

    expect(String(props['class'])).toContain('ecr-cell-out-of-window');
    expect(props['data-out-of-window']).toBe('true');
    expect(String(props['title'])).toContain('⟦grid.outOfWindowHint⟧');
  });

  it('значок стосується лише названої комірки, а без позначок сітка не змінюється', () => {
    const marked = columnsWith(new Set(['R1:C1']));
    const other = cellPropsFor(marked, 'R2');

    expect(String(other['class'] ?? '')).not.toContain(
      'ecr-cell-out-of-window',
    );
    expect(other['data-out-of-window']).toBeUndefined();

    const plain = cellPropsFor(columnsWith(new Set()), 'R1');
    expect(String(plain['class'] ?? '')).not.toContain(
      'ecr-cell-out-of-window',
    );
    expect(String(plain['title'] ?? '')).not.toContain('grid.outOfWindowHint');
  });
});

describe('applyPatchLocally → позначки поза вікном', () => {
  it('адреси з PatchCellsResponse.outOfWindow стають позначками свого зрізу', async () => {
    applyPatchLocally(new QueryClient(), request, response(['R1:C1']));

    await waitFor(() =>
      expect(outOfWindowMarksOf(7, 202609).has('R1:C1')).toBe(true),
    );
    expect(outOfWindowMarksOf(7, 202610).size).toBe(0);
    expect(outOfWindowMarksOf(8, 202609).size).toBe(0);
  });

  it('відповідь без outOfWindow (порожня, null чи старіший сервер) нічого не позначає', async () => {
    const client = new QueryClient();
    applyPatchLocally(client, request, response([]));
    applyPatchLocally(client, request, response(null));
    applyPatchLocally(client, request, response(undefined));

    // ⚠ Дати шанс ймовірному `import()` завершитися, перш ніж стверджувати «нічого».
    await import('../outOfWindowMarks');
    await new Promise((resolve) => setTimeout(resolve, 0));
    expect(outOfWindowMarksOf(7, 202609).size).toBe(0);
  });
});

describe('useOutOfWindowMarks', () => {
  it('перемальовує сітку, щойно прийшла нова відповідь, і лише додає позначки', () => {
    const { result } = renderHook(() => useOutOfWindowMarks(7, 202609));
    expect(result.current.size).toBe(0);

    act(() => recordOutOfWindow(7, 202609, ['R1:C1']));
    expect([...result.current]).toEqual(['R1:C1']);

    act(() => recordOutOfWindow(7, 202609, ['R2:C1']));
    expect([...result.current].sort()).toEqual(['R1:C1', 'R2:C1']);
  });

  it('позначки зрізу видно без жодного PATCH — тобто після перезавантаження', () => {
    // ⛔ Мутація «ігнорувати fromSlice» (стан до ФВ-2.16 у зрізі) лишає набір порожнім.
    const { result } = renderHook(() => useOutOfWindowMarks(7, 202609, ['R2:C1']));
    expect([...result.current]).toEqual(['R2:C1']);

    act(() => recordOutOfWindow(7, 202609, ['R1:C1']));
    expect([...result.current].sort()).toEqual(['R1:C1', 'R2:C1']);
  });

  it('той самий об\'єкт між рендерами, доки ні зріз, ні відповіді не змінилися', () => {
    const fromSlice = ['R2:C1'];
    const { result, rerender } = renderHook(() => useOutOfWindowMarks(7, 202609, fromSlice));
    const first = result.current;

    rerender();

    // ⚠ Сітка мемоізує колонки за посиланням: новий Set на кожен рендер
    // перебудовував би їх без причини.
    expect(result.current).toBe(first);
  });
});

describe('mergeOutOfWindow', () => {
  it('без позначок зрізу повертає позначки вкладки тим самим об\'єктом', () => {
    const recorded = new Set(['R1:C1']);

    expect(mergeOutOfWindow(undefined, recorded)).toBe(recorded);
    expect(mergeOutOfWindow(null, recorded)).toBe(recorded);
    expect(mergeOutOfWindow([], recorded)).toBe(recorded);
  });

  it('об\'єднує позначки зрізу й вкладки без дублікатів', () => {
    const merged = mergeOutOfWindow(['R1:C1', 'R2:C1'], new Set(['R1:C1', 'R3:C1']));

    expect([...merged].sort()).toEqual(['R1:C1', 'R2:C1', 'R3:C1']);
  });
});
