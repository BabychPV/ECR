import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  cellCoordinateOf,
  completeCellNavigation,
  currentCellNavigation,
  requestCellNavigation,
  revealGridCell,
  scrollIntoUpperThird,
  subscribeCellNavigation,
} from '../cellNavigation';

describe('scrollIntoUpperThird (fg6 F13: рядок — у верхній третині вікна, не на нижньому краї)', () => {
  function at(top: number): Element {
    const element = document.createElement('div');
    element.getBoundingClientRect = () => ({ top }) as DOMRect;

    return element;
  }

  function fakeWindow(): Window & { scrollBy: ReturnType<typeof vi.fn> } {
    return { innerHeight: 900, scrollBy: vi.fn() } as unknown as Window & { scrollBy: ReturnType<typeof vi.fn> };
  }

  it('рядок на нижньому краї (y=850 із 900) - сторінка прокручується так, щоб він став на y=300', () => {
    const win = fakeWindow();
    scrollIntoUpperThird(at(850), win);

    expect(win.scrollBy).toHaveBeenCalledWith({ top: 550 });
  });

  it('рядок уже вгорі (y=200) - сторінка не смикається', () => {
    const win = fakeWindow();
    scrollIntoUpperThird(at(200), win);

    expect(win.scrollBy).not.toHaveBeenCalled();
  });

  it('рядок вище за край вікна (y=-120) - повертається на третину', () => {
    const win = fakeWindow();
    scrollIntoUpperThird(at(-120), win);

    expect(win.scrollBy).toHaveBeenCalledWith({ top: -420 });
  });
});

/**
 * Перехід від зауваження перевірки до комірки (`ФВ-5.6`): сховище запиту,
 * координати в сітці, виклик API `revo-grid`.
 */
afterEach(() => {
  const pending = currentCellNavigation();
  if (pending !== null) completeCellNavigation(pending.seq);
});

const slice = {
  rows: [{ rowKey: 'r1' }, { rowKey: 'r2' }, { rowKey: 'r3' }],
  columns: [{ code: 'C1' }, { code: 'C2' }, { code: 'C3' }],
};

describe('сховище запиту переходу', () => {
  it('новий клік замінює попередній, і завершення старого нового не стирає', () => {
    const listener = vi.fn();
    const unsubscribe = subscribeCellNavigation(listener);

    requestCellNavigation({ tableDefId: 7, rowKey: 'r1', columnCode: 'C1' });
    const first = currentCellNavigation();
    requestCellNavigation({ tableDefId: 8, rowKey: 'r2', columnCode: null });
    const second = currentCellNavigation();

    expect(first).not.toBeNull();
    expect(second?.tableDefId).toBe(8);
    expect(listener).toHaveBeenCalledTimes(2);

    // ⛔ Сітка першої таблиці доїхала пізніше за другий клік — вона не має
    // права скасувати перехід, якого людина хоче зараз.
    completeCellNavigation(first?.seq ?? -1);
    expect(currentCellNavigation()).toBe(second);

    completeCellNavigation(second?.seq ?? -1);
    expect(currentCellNavigation()).toBeNull();

    unsubscribe();
  });

  it('повторний клік по тому самому зауваженню — новий запит, не «вже зроблено»', () => {
    const target = { tableDefId: 7, rowKey: 'r1', columnCode: 'C1' };

    requestCellNavigation(target);
    const first = currentCellNavigation();
    completeCellNavigation(first?.seq ?? -1);

    requestCellNavigation(target);
    const again = currentCellNavigation();

    expect(again).not.toBeNull();
    expect(again?.seq).not.toBe(first?.seq);
  });

  it('копіює лише адресу, а не весь об’єкт зауваження', () => {
    requestCellNavigation({
      tableDefId: 7,
      rowKey: 'r1',
      columnCode: 'C1',
      message: 'зайве',
    } as never);

    expect(Object.keys(currentCellNavigation() ?? {}).sort()).toEqual([
      'columnCode',
      'rowKey',
      'seq',
      'tableDefId',
    ]);
  });
});

describe('координати адреси в сітці', () => {
  it('рядок і колонка — за ключем і кодом', () => {
    expect(cellCoordinateOf(slice, false, 'r2', 'C3')).toEqual({ x: 2, y: 1 });
  });

  it('колонка підпису рядків зсуває x на одиницю праворуч', () => {
    // ⛔ Без зсуву фокус став би на колонку лівіше за адресу зауваження.
    expect(cellCoordinateOf(slice, true, 'r2', 'C3')).toEqual({ x: 3, y: 1 });
  });

  it('зауваження до рядка — перша колонка даних, а не колонка підпису', () => {
    expect(cellCoordinateOf(slice, false, 'r3', null)).toEqual({ x: 0, y: 2 });
    expect(cellCoordinateOf(slice, true, 'r3', null)).toEqual({ x: 1, y: 2 });
  });

  it('невідома колонка — перехід до рядка, невідомий рядок — нікуди', () => {
    expect(cellCoordinateOf(slice, false, 'r1', 'GONE')).toEqual({ x: 0, y: 0 });
    expect(cellCoordinateOf(slice, false, 'missing', 'C1')).toBeNull();
  });
});

describe('прокрутка й фокус через API revo-grid', () => {
  it('чекає готовності елемента, прокручує і ставить фокус на ту саму комірку', async () => {
    const calls: string[] = [];
    const container = document.createElement('div');
    const grid = Object.assign(document.createElement('revo-grid'), {
      componentOnReady: vi.fn(() => {
        calls.push('ready');
        return Promise.resolve();
      }),
      scrollToCoordinate: vi.fn(() => {
        calls.push('scroll');
        return Promise.resolve();
      }),
      setCellsFocus: vi.fn(() => {
        calls.push('focus');
        return Promise.resolve();
      }),
    });
    container.append(grid);

    await expect(revealGridCell(container, { x: 3, y: 1 })).resolves.toBe(true);

    expect(calls).toEqual(['ready', 'scroll', 'focus']);
    expect(grid.scrollToCoordinate).toHaveBeenCalledWith({ x: 3, y: 1 });
    expect(grid.setCellsFocus).toHaveBeenCalledWith({ x: 3, y: 1 }, { x: 3, y: 1 });
  });

  it('закріплені колонки: прокручувана ціль іде в rgCol зі зсувом, закріплена - в colPinStart без прокрутки по x', async () => {
    const container = document.createElement('div');
    const grid = Object.assign(document.createElement('revo-grid'), {
      componentOnReady: vi.fn(() => Promise.resolve()),
      scrollToCoordinate: vi.fn(() => Promise.resolve()),
      setCellsFocus: vi.fn(() => Promise.resolve()),
    });
    container.append(grid);

    // Глобальна колонка 5 при 3 закріплених - це 3-тя колонка прокручуваного в'юпорту.
    await revealGridCell(container, { x: 5, y: 2 }, 3);
    expect(grid.scrollToCoordinate).toHaveBeenLastCalledWith({ x: 2, y: 2 });
    expect(grid.setCellsFocus).toHaveBeenLastCalledWith({ x: 2, y: 2 }, { x: 2, y: 2 });

    // Глобальна колонка 1 - у закріпленому в'юпорті; по x прокручувати нічого.
    await revealGridCell(container, { x: 1, y: 2 }, 3);
    expect(grid.scrollToCoordinate).toHaveBeenLastCalledWith({ y: 2 });
    expect(grid.setCellsFocus).toHaveBeenLastCalledWith({ x: 1, y: 2 }, { x: 1, y: 2 }, 'colPinStart');
  });

  it('без елемента сітки — false, без винятку', async () => {
    await expect(revealGridCell(document.createElement('div'), { x: 0, y: 0 })).resolves.toBe(false);
  });
});
