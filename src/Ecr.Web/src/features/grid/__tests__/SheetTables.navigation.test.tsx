import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import type { DocumentTableDto } from '@/api/types';
import {
  completeCellNavigation,
  currentCellNavigation,
  requestCellNavigation,
  type CellNavigationRequest,
} from '../cellNavigation';
import { SheetTables } from '../SheetTables';

/**
 * `ФВ-5.6`: клік по зауваженню веде до таблиці, навіть якщо її сітка ще не
 * змонтована (за 90 таблицями нижче — `SheetTables.tsx`).
 *
 * ⚠ `DocumentGrid` підмінений: тут перевіряється рішення аркуша — яку сітку
 * змонтувати, до якого слота прокрутити і КОМУ передати запит комірки.
 * Сам фокус комірки — `DocumentGrid.cellNavigation.test.tsx`.
 */
const gridProps = new Map<number, CellNavigationRequest | null | undefined>();

vi.mock('../DocumentGrid', () => ({
  DocumentGrid: (props: { tableInstanceId: number; navigateTo?: CellNavigationRequest | null }) => {
    gridProps.set(props.tableInstanceId, props.navigateTo);

    return <div data-testid={`grid-${String(props.tableInstanceId)}`} />;
  },
}));

function tableFixture(index: number, tableDefId = index): DocumentTableDto {
  return {
    allowsDynamicRows: false,
    maxDynamicRows: null,
    sheetCode: 'S1',
    sheetDefId: 1,
    sheetNameL10n: { values: { en: 'Sheet' } },
    sheetOrdinal: 1,
    tableCode: `T${String(index)}`,
    tableDefId,
    tableInstanceId: 1000 + index,
    tableNameL10n: { values: { en: `Table ${String(index)}` } },
    tableOrdinal: index,
  } as DocumentTableDto;
}

const tables = Array.from({ length: 91 }, (_, index) => tableFixture(index));

/** Спостерігач, що не повідомляє нічого: «прокрутки не було». */
class SilentObserver {
  observe(): void {}
  unobserve(): void {}
  disconnect(): void {}
  takeRecords(): IntersectionObserverEntry[] {
    return [];
  }
}

let scrolled: Element[];

beforeEach(() => {
  gridProps.clear();
  scrolled = [];
  vi.stubGlobal('IntersectionObserver', SilentObserver);

  // jsdom не має `scrollIntoView` (заглушка — `src/test/setup.ts`): шпигун
  // пише, КУДИ прокрутили.
  vi.spyOn(HTMLElement.prototype, 'scrollIntoView').mockImplementation(function scrollIntoView(
    this: HTMLElement,
  ) {
    scrolled.push(this);
  });
});

afterEach(() => {
  const pending = currentCellNavigation();
  if (pending !== null) completeCellNavigation(pending.seq);
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

function renderSheet(list: readonly DocumentTableDto[] = tables): ReturnType<typeof render> {
  return render(
    <MantineProvider>
      <SheetTables documentId={7} periodKey={202609} readOnly={false} tables={list} />
    </MantineProvider>,
  );
}

describe('перехід від зауваження до таблиці аркуша', () => {
  it('монтує сітку цільової таблиці без прокрутки, прокручує до її слота і передає їй запит', () => {
    renderSheet();
    expect(screen.queryByTestId('grid-1080')).toBeNull();

    act(() => requestCellNavigation({ tableDefId: 80, rowKey: 'r2', columnCode: 'C1' }));

    // ⛔ Саме ця сітка — і лише вона: решта 90 лишаються заглушками.
    expect(screen.getByTestId('grid-1080')).toBeDefined();
    expect(document.querySelectorAll('[data-testid^="grid-"]')).toHaveLength(1);

    expect(scrolled).toHaveLength(1);
    expect(scrolled[0]?.getAttribute('data-table-slot')).toBe('1080');

    expect(gridProps.get(1080)).toMatchObject({ tableDefId: 80, rowKey: 'r2', columnCode: 'C1' });

    // Запит ще живий: його завершить сітка, коли поставить фокус.
    expect(currentCellNavigation()).not.toBeNull();
  });

  it('зауваження до таблиці цілком завершується прокруткою, сітці запиту не дає', () => {
    renderSheet();

    act(() => requestCellNavigation({ tableDefId: 5, rowKey: null, columnCode: null }));

    expect(scrolled[0]?.getAttribute('data-table-slot')).toBe('1005');
    expect(gridProps.get(1005)).toBeNull();
    expect(currentCellNavigation()).toBeNull();
  });

  it('два екземпляри одного опису таблиці — ціль перший, другий запиту не бачить', () => {
    renderSheet([tableFixture(0, 50), tableFixture(1, 50)]);

    act(() => requestCellNavigation({ tableDefId: 50, rowKey: 'r1', columnCode: null }));

    expect(gridProps.get(1000)).toMatchObject({ tableDefId: 50 });
    expect(screen.queryByTestId('grid-1001')).toBeNull();
  });

  it('таблиці на аркуші немає — запит чекає нового аркуша, а не зникає', () => {
    const view = renderSheet([tableFixture(0)]);

    act(() => requestCellNavigation({ tableDefId: 42, rowKey: 'r1', columnCode: 'C1' }));
    expect(scrolled).toHaveLength(0);
    expect(currentCellNavigation()).not.toBeNull();

    // Сторінка перемкнула аркуш — прийшли його таблиці.
    view.rerender(
      <MantineProvider>
        <SheetTables
          documentId={7}
          periodKey={202609}
          readOnly={false}
          tables={[tableFixture(3, 42)]}
        />
      </MantineProvider>,
    );

    expect(scrolled[0]?.getAttribute('data-table-slot')).toBe('1003');
    expect(gridProps.get(1003)).toMatchObject({ tableDefId: 42, rowKey: 'r1' });
  });

  it('прокрутка — одна на клік: повторний рендер аркуша сторінку не смикає', () => {
    const view = renderSheet();

    act(() => requestCellNavigation({ tableDefId: 10, rowKey: 'r1', columnCode: 'C1' }));
    view.rerender(
      <MantineProvider>
        <SheetTables documentId={7} periodKey={202609} readOnly={false} tables={tables} />
      </MantineProvider>,
    );

    expect(scrolled).toHaveLength(1);
  });
});
