import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  cellCoordinateOf,
  completeCellNavigation,
  currentCellNavigation,
  requestCellNavigation,
  revealGridCell,
  subscribeCellNavigation,
} from '../cellNavigation';

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

  it('без елемента сітки — false, без винятку', async () => {
    await expect(revealGridCell(document.createElement('div'), { x: 0, y: 0 })).resolves.toBe(false);
  });
});
