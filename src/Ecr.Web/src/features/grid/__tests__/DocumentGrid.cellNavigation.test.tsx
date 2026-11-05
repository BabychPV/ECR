import { createElement } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, render, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ColumnRegular } from '@revolist/revogrid';
import type { ColumnDto, TableSliceDto } from '@/api/types';
import { cancelAutosave } from '../autosave';
import { resetPending } from '../pendingStore';
import {
  NavigationHighlightMs,
  completeCellNavigation,
  currentCellNavigation,
  requestCellNavigation,
  type CellNavigationRequest,
} from '../cellNavigation';
import { DocumentGrid } from '../DocumentGrid';

/**
 * `ФВ-5.6`: сітка, отримавши запит переходу, прокручує до комірки, ставить на
 * неї фокус RevoGrid і підсвічує її.
 *
 * ⚠ RevoGrid підмінено, але так, щоб у DOM був справжній елемент `revo-grid`
 * з тими самими методами, що в бібліотеки (`scrollToCoordinate`,
 * `setCellsFocus`): сітка шукає його в контейнері так само, як у застосунку.
 * Опис колонок заглушка віддає назовні — підсвітка перевіряється через
 * `cellProperties`, тобто рівно тим шляхом, яким клас потрапляє на комірку.
 */
const revo = {
  scrollToCoordinate: vi.fn(() => Promise.resolve()),
  setCellsFocus: vi.fn(() => Promise.resolve()),
};

let lastColumns: ColumnRegular[] = [];

vi.mock('@revolist/react-datagrid', () => ({
  RevoGrid: (props: { columns?: ColumnRegular[] }) => {
    lastColumns = props.columns ?? [];

    return createElement('revo-grid', {
      'data-testid': 'revogrid-stub',
      ref: (node: HTMLElement | null) => {
        if (node !== null) Object.assign(node, revo);
      },
    });
  },
}));

function column(code: string, ordinal: number): ColumnDto {
  return {
    code,
    dataType: 'Decimal',
    defaultValue: null,
    displayFormat: null,
    header: code,
    id: ordinal + 1,
    isReadOnly: false,
    isRequired: false,
    isRequiredByMethodology: false,
    lookupRegistryDefId: null,
    ordinal,
    scale: 2,
    unitId: null,
    unitSymbol: null,
  };
}

function row(rowKey: string, ordinal: number, label: string | null) {
  return {
    cells: { C1: '1', C2: '2' },
    isOrphaned: false,
    label,
    ordinal,
    rowKey,
    rowVersion: 'v1',
    rowKind: 'Item',
  };
}

let labeled = false;

function sliceFixture(): TableSliceDto {
  return {
    cellConfirmations: {},
    cellPermissions: {},
    periodKey: 202609,
    tableInstanceId: 1,
    columns: [column('C1', 0), column('C2', 1)],
    rows: [row('r1', 0, labeled ? 'Fuel' : null), row('r2', 1, labeled ? 'Gas' : null)],
  };
}

function mockServer(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(() =>
      Promise.resolve(
        new Response(JSON.stringify(sliceFixture()), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        }),
      ),
    ),
  );
}

function show(navigateTo: CellNavigationRequest | null): {
  navigate: (target: CellNavigationRequest | null) => void;
} {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  const tree = (target: CellNavigationRequest | null) => (
    <MantineProvider>
      <QueryClientProvider client={client}>
        <DocumentGrid
          documentId={1}
          tableInstanceId={1}
          tableDefId={9}
          periodKey={202609}
          readOnly={false}
          allowsDynamicRows={false}
          maxDynamicRows={null}
          navigateTo={target}
        />
      </QueryClientProvider>
    </MantineProvider>
  );

  const view = render(tree(navigateTo));

  return { navigate: (target) => view.rerender(tree(target)) };
}

/** Клас, який `cellProperties` дає комірці (рядок, код колонки). */
function classOf(rowKey: string, code: string): string {
  const found = lastColumns.find((candidate) => candidate.prop === code);
  const props = found?.cellProperties?.({
    model: { __rowKey: rowKey },
    prop: code,
    rowIndex: 0,
    colIndex: 0,
    column: found,
    data: [],
    type: 'rgRow',
    colType: 'rgCol',
  } as never) as { class?: string } | undefined;

  return props?.class ?? '';
}

function request(target: Omit<CellNavigationRequest, 'seq'>): CellNavigationRequest {
  requestCellNavigation(target);
  const current = currentCellNavigation();
  if (current === null) throw new Error('запит не поставлено');

  return current;
}

beforeEach(() => {
  labeled = false;
  lastColumns = [];
  revo.scrollToCoordinate.mockClear();
  revo.setCellsFocus.mockClear();
  mockServer();
});

afterEach(() => {
  const pending = currentCellNavigation();
  if (pending !== null) completeCellNavigation(pending.seq);
  cancelAutosave();
  resetPending();
  vi.useRealTimers();
  vi.unstubAllGlobals();
  localStorage.clear();
});

describe('перехід до комірки в сітці (ФВ-5.6)', () => {
  it('прокручує, ставить фокус на комірку адреси, підсвічує її і завершує запит', async () => {
    const target = request({ tableDefId: 9, rowKey: 'r2', columnCode: 'C2' });
    show(target);

    await waitFor(() => expect(revo.setCellsFocus).toHaveBeenCalled());

    expect(revo.scrollToCoordinate).toHaveBeenCalledWith({ x: 1, y: 1 });
    expect(revo.setCellsFocus).toHaveBeenCalledWith({ x: 1, y: 1 }, { x: 1, y: 1 });

    // ⛔ Підсвічена рівно адреса зауваження — не сусідня комірка й не рядок.
    await waitFor(() => expect(classOf('r2', 'C2')).toContain('ecr-cell-nav-target'));
    expect(classOf('r2', 'C1')).not.toContain('ecr-cell-nav-target');
    expect(classOf('r1', 'C2')).not.toContain('ecr-cell-nav-target');

    await waitFor(() => expect(currentCellNavigation()).toBeNull());
  });

  it('з колонкою підписів рядків фокус зсунутий на одну колонку праворуч', async () => {
    labeled = true;
    show(request({ tableDefId: 9, rowKey: 'r1', columnCode: 'C1' }));

    // x=1: колонка 0 — підпис рядка, C1 — перша колонка даних.
    await waitFor(() => expect(revo.setCellsFocus).toHaveBeenCalledWith({ x: 1, y: 0 }, { x: 1, y: 0 }));
    await waitFor(() => expect(classOf('r1', 'C1')).toContain('ecr-cell-nav-target'));
  });

  it('зауваження до рядка — фокус і підсвітка на першій колонці даних', async () => {
    show(request({ tableDefId: 9, rowKey: 'r2', columnCode: null }));

    await waitFor(() => expect(revo.setCellsFocus).toHaveBeenCalledWith({ x: 0, y: 1 }, { x: 0, y: 1 }));
    await waitFor(() => expect(classOf('r2', 'C1')).toContain('ecr-cell-nav-target'));
  });

  it('рядка в зрізі немає — нікуди не стрибає і запит завершує', async () => {
    show(request({ tableDefId: 9, rowKey: 'deleted', columnCode: 'C1' }));

    await waitFor(() => expect(currentCellNavigation()).toBeNull());
    expect(revo.setCellsFocus).not.toHaveBeenCalled();
    expect(lastColumns.length).toBeGreaterThan(0);
    expect(classOf('r1', 'C1')).not.toContain('ecr-cell-nav-target');
  });

  it('підсвітка згасає сама, а повторний перехід запалює її знову', async () => {
    // ⚠ Годинник підмінено ДО переходу: таймер згасання ставиться в мить
    // підсвітки, і справжній, поставлений раніше, фальшивий уже не прокрутить.
    vi.useFakeTimers({ shouldAdvanceTime: true });

    const view = show(request({ tableDefId: 9, rowKey: 'r1', columnCode: 'C2' }));
    await waitFor(() => expect(classOf('r1', 'C2')).toContain('ecr-cell-nav-target'));

    act(() => {
      vi.advanceTimersByTime(NavigationHighlightMs - 100);
    });
    expect(classOf('r1', 'C2')).toContain('ecr-cell-nav-target');

    act(() => {
      vi.advanceTimersByTime(100);
    });
    expect(classOf('r1', 'C2')).not.toContain('ecr-cell-nav-target');

    view.navigate(request({ tableDefId: 9, rowKey: 'r1', columnCode: 'C2' }));
    await waitFor(() => expect(classOf('r1', 'C2')).toContain('ecr-cell-nav-target'));
    expect(revo.setCellsFocus).toHaveBeenCalledTimes(2);
  });

  it('без запиту сітка нічого не прокручує', async () => {
    show(null);

    await waitFor(() => expect(lastColumns.length).toBeGreaterThan(0));
    expect(revo.scrollToCoordinate).not.toHaveBeenCalled();
  });
});
