import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  cellCoordinateOf,
  completeCellNavigation,
  FirstRowKey,
  currentCellNavigation,
  requestCellNavigation,
  keepInUpperThird,
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

  it('x7: рядок ставиться у верхню третину ОДИН раз, у наступному кадрі — без утримання 40 кадрів', () => {
    const queue: FrameRequestCallback[] = [];
    vi.spyOn(window, 'requestAnimationFrame').mockImplementation((cb) => queue.push(cb));
    vi.spyOn(window, 'cancelAnimationFrame').mockImplementation(() => undefined);
    const scrollBy = vi.spyOn(window, 'scrollBy').mockImplementation(() => undefined);
    Object.defineProperty(window, 'innerHeight', { value: 900, configurable: true });
    const flush = (n: number): void => {
      for (let i = 0; i < n; i++) queue.shift()?.(0);
    };

    keepInUpperThird(() => at(850));
    expect(scrollBy).not.toHaveBeenCalled();
    flush(5);
    expect(scrollBy).toHaveBeenCalledTimes(1);
    expect(queue).toHaveLength(0);
    vi.restoreAllMocks();
  });

  it('людина прокрутила до кадру — сторінку не смикаємо', () => {
    const queue: FrameRequestCallback[] = [];
    vi.spyOn(window, 'requestAnimationFrame').mockImplementation((cb) => queue.push(cb));
    const cancel = vi.spyOn(window, 'cancelAnimationFrame').mockImplementation(() => {
      queue.length = 0;
    });
    const scrollBy = vi.spyOn(window, 'scrollBy').mockImplementation(() => undefined);

    keepInUpperThird(() => at(850));
    window.dispatchEvent(new Event('wheel'));
    queue.shift()?.(0);

    expect(cancel).toHaveBeenCalled();
    expect(scrollBy).not.toHaveBeenCalled();
    vi.restoreAllMocks();
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

  // ✎ Огляд M4 (4): `scrollToCoordinate` RevoGrid — у ПІКСЕЛЯХ; колонку за межами вікна (M10 з 12) прокручує лише
  // `scrollToColumnIndex` за індексом в'юпорту rgCol (e2e-stand 45310768143: фокус на M10, а видно M01–M08).
  it('прокручувана колонка: scrollToColumnIndex з індексом в`юпорту; закріплена — без прокрутки колонки', async () => {
    const container = document.createElement('div');
    const grid = Object.assign(document.createElement('revo-grid'), {
      componentOnReady: vi.fn(() => Promise.resolve()),
      scrollToCoordinate: vi.fn(() => Promise.resolve()),
      scrollToColumnIndex: vi.fn(() => Promise.resolve()),
      setCellsFocus: vi.fn(() => Promise.resolve()),
    });
    container.append(grid);

    await revealGridCell(container, { x: 10, y: 0 }, 1);
    expect(grid.scrollToColumnIndex).toHaveBeenLastCalledWith(9);

    grid.scrollToColumnIndex.mockClear();
    await revealGridCell(container, { x: 0, y: 0 }, 1);
    expect(grid.scrollToColumnIndex).not.toHaveBeenCalled();
  });

  it('прокрутку колонки скинуто після переходу (перемальовка колонок) — прокручується знову', async () => {
    const container = document.createElement('div');
    const viewport = document.createElement('revogr-viewport-scroll');
    viewport.className = 'rgCol';
    let calls = 0;
    const grid = Object.assign(document.createElement('revo-grid'), {
      componentOnReady: vi.fn(() => Promise.resolve()),
      scrollToCoordinate: vi.fn(() => Promise.resolve()),
      // Перший виклик «скинуто» (scrollLeft лишається 0), другий — прокрутка тримається.
      scrollToColumnIndex: vi.fn(() => {
        calls += 1;
        if (calls >= 2) viewport.scrollLeft = 300;
        return Promise.resolve();
      }),
      setCellsFocus: vi.fn(() => Promise.resolve()),
    });
    grid.append(viewport);
    container.append(grid);

    await revealGridCell(container, { x: 10, y: 0 }, 1);
    await vi.waitFor(() => expect(grid.scrollToColumnIndex.mock.calls.length).toBeGreaterThanOrEqual(2), { timeout: 2000 });
    expect(viewport.scrollLeft).toBe(300);
  });

  // ✎ Огляд b11 (F): перехід торкається КОЖНОГО стрибка (Issues, чат, D-461) — не смикати видиме, не затримувати фокус,
  // не повертати до старої колонки.
  function revoWithViewport(options: { columnVisible?: boolean; rowVisible?: boolean } = {}) {
    const container = document.createElement('div');
    const viewport = document.createElement('revogr-viewport-scroll');
    viewport.className = 'rgCol';
    const rect = (left: number, top: number, width: number, height: number) => () =>
      ({ left, top, right: left + width, bottom: top + height, width, height, x: left, y: top, toJSON: () => ({}) }) as DOMRect;
    viewport.getBoundingClientRect = rect(0, 0, 800, 400);
    const cell = document.createElement('div');
    cell.setAttribute('data-rgcol', '9');
    cell.setAttribute('data-rgrow', '30');
    cell.getBoundingClientRect = rect(options.columnVisible === true ? 100 : 900, options.rowVisible === true ? 100 : 900, 80, 30);
    const data = document.createElement('revogr-data');
    data.setAttribute('type', 'rgRow');
    data.append(cell);
    viewport.append(data);
    const grid = Object.assign(document.createElement('revo-grid'), {
      componentOnReady: vi.fn(() => Promise.resolve()),
      scrollToCoordinate: vi.fn(() => Promise.resolve()),
      scrollToColumnIndex: vi.fn(() => Promise.resolve()),
      scrollToRow: vi.fn(() => Promise.resolve()),
      setCellsFocus: vi.fn(() => Promise.resolve()),
    });
    grid.append(viewport);
    container.append(grid);
    return { container, grid, viewport };
  }

  it('(a) колонка й рядок уже у вікні — без прокрутки колонки й рядка; поза вікном — прокрутка за індексом', async () => {
    const visible = revoWithViewport({ columnVisible: true, rowVisible: true });
    visible.viewport.scrollLeft = 50;
    await revealGridCell(visible.container, { x: 10, y: 30 }, 1);
    expect(visible.grid.scrollToColumnIndex).not.toHaveBeenCalled();
    expect(visible.grid.scrollToRow).not.toHaveBeenCalled();
    expect(visible.grid.setCellsFocus).toHaveBeenCalledWith({ x: 9, y: 30 }, { x: 9, y: 30 });

    const hidden = revoWithViewport();
    await revealGridCell(hidden.container, { x: 10, y: 30 }, 1);
    expect(hidden.grid.scrollToColumnIndex).toHaveBeenCalledWith(9);
    // Вертикаль: `scrollToCoordinate({ y })` — теж ПІКСЕЛІ; рядок за індексом — `scrollToRow`.
    expect(hidden.grid.scrollToRow).toHaveBeenCalledWith(30);
    expect(hidden.grid.scrollToCoordinate).not.toHaveBeenCalled();
  });

  it('(b) фокус не чекає на сторожа горизонтальної прокрутки (повертається одразу)', async () => {
    const { container, grid } = revoWithViewport();
    const started = Date.now();
    await revealGridCell(container, { x: 10, y: 0 }, 1);
    expect(Date.now() - started).toBeLessThan(100);
    expect(grid.setCellsFocus).toHaveBeenCalled();
    // Сторож працює далі у фоні: прокрутку скинуто (scrollLeft 0) — повтор.
    await vi.waitFor(() => expect(grid.scrollToColumnIndex.mock.calls.length).toBeGreaterThanOrEqual(2), { timeout: 2000 });
  });

  it('(c) новий стрибок скасовує сторожа попереднього — прокрутка не повертається до старої колонки', async () => {
    const { container, grid } = revoWithViewport();
    await revealGridCell(container, { x: 10, y: 0 }, 1);
    await revealGridCell(container, { x: 4, y: 0 }, 1);
    grid.scrollToColumnIndex.mockClear();
    await new Promise((resolve) => setTimeout(resolve, 1200));
    expect(grid.scrollToColumnIndex.mock.calls.length).toBeGreaterThan(0);
    expect((grid.scrollToColumnIndex.mock.calls as unknown as number[][]).every((call) => call[0] === 3)).toBe(true);
  });

  it('без елемента сітки — false, без винятку', async () => {
    await expect(revealGridCell(document.createElement('div'), { x: 0, y: 0 })).resolves.toBe(false);
  });
});

describe('cellCoordinateOf: FirstRowKey (огляд M4 (4) — перехід до колонки місяця річного документа)', () => {
  const slice = { rows: [{ rowKey: 'r1' }, { rowKey: 'r2' }], columns: [{ code: 'M01' }, { code: 'M10' }] };

  it('перший рядок зрізу в колонці; порожня таблиця — null (лишаємось на таблиці)', () => {
    expect(cellCoordinateOf(slice, true, FirstRowKey, 'M10')).toEqual({ x: 2, y: 0 });
    expect(cellCoordinateOf({ rows: [], columns: slice.columns }, true, FirstRowKey, 'M10')).toBeNull();
  });
});