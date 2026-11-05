import { describe, expect, it, vi } from 'vitest';
import {
  clampSelection,
  focusedCellOfEvent,
  globalColumnOf,
  selectionOfFocusEvent,
  selectionOfRangeEvent,
  trackFocusedCell,
  trackSelection,
  viewportOfColumn,
} from '../selection';

/**
 * Розбір подій виділення RevoGrid (аудит §10.1).
 *
 * ⚠ Чистий модуль перевіряється без сітки — так само, як `clipboard.ts` і
 * `undo.ts`, і з тієї самої причини: саме ці індекси визначають, у які комірки
 * ляже вставка, а логіка, схована в колбеку веб-компонента, перевірялася б
 * лише через його тіньове дерево.
 *
 * ⚠ Форма `detail` — дослівно та, що в типах бібліотеки:
 * `focuscell` → `ApplyFocusEvent & FocusRenderEvent` (`focus`/`end` типу `Cell
 * {x, y}`, плюс `rowType`/`colType`), `setrange` → `RangeArea & { type }`
 * (`{x, y, x1, y1}`).
 */
describe('selectionOfFocusEvent: клік по комірці', () => {
  it('дає якір і вироджений діапазон зі самої комірки', () => {
    const selection = selectionOfFocusEvent({
      rowType: 'rgRow',
      colType: 'rgCol',
      focus: { x: 2, y: 49 },
      end: { x: 2, y: 49 },
    });

    expect(selection).toEqual({
      anchor: { rowIndex: 49, columnIndex: 2 },
      range: { fromRow: 49, toRow: 49, fromColumn: 2, toColumn: 2 },
    });
  });

  it('без `end` діапазон усе одно є — це сама сфокусована комірка', () => {
    expect(selectionOfFocusEvent({ focus: { x: 1, y: 1 } })?.range).toEqual({
      fromRow: 1,
      toRow: 1,
      fromColumn: 1,
      toColumn: 1,
    });
  });

  it('виділення вгору/вліво нормалізується: `from` завжди менший кут', () => {
    // Shift+↑: фокус лишається в комірці, з якої почали, а `end` вище неї.
    const selection = selectionOfFocusEvent({
      focus: { x: 3, y: 7 },
      end: { x: 1, y: 4 },
    });

    expect(selection?.range).toEqual({ fromRow: 4, toRow: 7, fromColumn: 1, toColumn: 3 });

    // ⚠ Якір — саме кут прямокутника, а не комірка фокуса: вставка від
    // «нижнього» кінця виділення пішла б у протилежний бік від видимого.
    expect(selection?.anchor).toEqual({ rowIndex: 4, columnIndex: 1 });
  });

  it('подія закріплених РЯДКІВ і колонок справа ІГНОРУЄТЬСЯ', () => {
    // ⛔ Індекси там відлічуються від своєї секції, не від зрізу: прийняти їх
    // як звичайні означало б записати вставку в зовсім інший рядок.
    expect(
      selectionOfFocusEvent({ rowType: 'rowPinStart', colType: 'rgCol', focus: { x: 0, y: 0 } }),
    ).toBeNull();
    expect(
      selectionOfFocusEvent({ rowType: 'rgRow', colType: 'colPinEnd', focus: { x: 0, y: 0 } }),
    ).toBeNull();
  });

  it('закріплені колонки зліва: `colPinStart` -> x, `rgCol` -> x + кількість закріплених', () => {
    // ⛔ `x` відлічується від початку СВОГО в'юпорту. Без зсуву клік по 3-й прокручуваній колонці
    // при 2 закріплених вказував би на 1-шу (глобальний індекс 0 замість 2).
    const pinned = selectionOfFocusEvent(
      { rowType: 'rgRow', colType: 'colPinStart', focus: { x: 1, y: 4 }, end: { x: 1, y: 4 } },
      3,
    );
    expect(pinned?.anchor).toEqual({ rowIndex: 4, columnIndex: 1 });

    const scrolled = selectionOfFocusEvent(
      { rowType: 'rgRow', colType: 'rgCol', focus: { x: 0, y: 4 }, end: { x: 2, y: 5 } },
      3,
    );
    expect(scrolled).toEqual({
      anchor: { rowIndex: 4, columnIndex: 3 },
      range: { fromRow: 4, toRow: 5, fromColumn: 3, toColumn: 5 },
    });
  });

  it('globalColumnOf і viewportOfColumn - взаємно зворотні на межі закріплення', () => {
    expect(globalColumnOf('colPinStart', 2, 3)).toBe(2);
    expect(globalColumnOf('rgCol', 0, 3)).toBe(3);
    expect(globalColumnOf(undefined, 4, 3)).toBe(7);
    expect(globalColumnOf('colPinEnd', 0, 3)).toBeNull();
    expect(globalColumnOf('rgCol', -1, 3)).toBeNull();

    expect(viewportOfColumn(2, 3)).toEqual({ colType: 'colPinStart', x: 2 });
    expect(viewportOfColumn(3, 3)).toEqual({ colType: 'rgCol', x: 0 });
    expect(viewportOfColumn(5, 0)).toEqual({ colType: 'rgCol', x: 5 });
  });

  it('подія без координат або з неціліми/від\'ємними — `null`, а не (0,0)', () => {
    expect(selectionOfFocusEvent(undefined)).toBeNull();
    expect(selectionOfFocusEvent({})).toBeNull();
    expect(selectionOfFocusEvent({ focus: { x: 1 } })).toBeNull();
    expect(selectionOfFocusEvent({ focus: { x: 1.5, y: 2 } })).toBeNull();
    expect(selectionOfFocusEvent({ focus: { x: -1, y: 2 } })).toBeNull();
  });
});

describe('selectionOfRangeEvent: виділення діапазону', () => {
  it('дає прямокутник і якір у його куті', () => {
    expect(selectionOfRangeEvent({ type: 'rgRow', x: 1, y: 1, x1: 2, y1: 3 })).toEqual({
      anchor: { rowIndex: 1, columnIndex: 1 },
      range: { fromRow: 1, toRow: 3, fromColumn: 1, toColumn: 2 },
    });
  });

  it('протягування знизу вгору нормалізується', () => {
    expect(selectionOfRangeEvent({ x: 4, y: 9, x1: 2, y1: 5 })?.range).toEqual({
      fromRow: 5,
      toRow: 9,
      fromColumn: 2,
      toColumn: 4,
    });
  });

  it('неповний прямокутник — `null`', () => {
    expect(selectionOfRangeEvent({ x: 1, y: 1, x1: 2 })).toBeNull();
    expect(selectionOfRangeEvent(null)).toBeNull();
  });

  it('діапазон усередині прокручуваного в\'юпорту зсувається на кількість закріплених, закріпленого - ні', () => {
    expect(selectionOfRangeEvent({ type: 'rgRow', x: 0, y: 1, x1: 1, y1: 2 }, 2, 'rgCol')?.range).toEqual({
      fromRow: 1,
      toRow: 2,
      fromColumn: 2,
      toColumn: 3,
    });
    expect(selectionOfRangeEvent({ type: 'rgRow', x: 0, y: 1, x1: 1, y1: 2 }, 2, 'colPinStart')?.range).toEqual({
      fromRow: 1,
      toRow: 2,
      fromColumn: 0,
      toColumn: 1,
    });
    expect(selectionOfRangeEvent({ x: 0, y: 0, x1: 0, y1: 0 }, 2, 'colPinEnd')).toBeNull();
  });
});

describe('trackSelection: підписка на події, що виходять із тіньового дерева', () => {
  it('ловить `focuscell` і `setrange`, які піднялися з нащадка', () => {
    const container = document.createElement('div');
    const grid = document.createElement('div');
    container.appendChild(grid);
    document.body.appendChild(container);

    const report = vi.fn();
    const stop = trackSelection(container, report);

    // ⚠ Саме `bubbles: true, composed: true` — рівно так їх оголошує
    // `revogr-overlay-selection`, і саме тому слухач на контейнері їх бачить.
    grid.dispatchEvent(
      new CustomEvent('focuscell', {
        bubbles: true,
        composed: true,
        detail: { rowType: 'rgRow', colType: 'rgCol', focus: { x: 1, y: 2 }, end: { x: 1, y: 2 } },
      }),
    );

    expect(report).toHaveBeenCalledWith({
      anchor: { rowIndex: 2, columnIndex: 1 },
      range: { fromRow: 2, toRow: 2, fromColumn: 1, toColumn: 1 },
    });

    grid.dispatchEvent(
      new CustomEvent('setrange', {
        bubbles: true,
        composed: true,
        detail: { type: 'rgRow', x: 0, y: 0, x1: 1, y1: 1 },
      }),
    );

    expect(report).toHaveBeenLastCalledWith({
      anchor: { rowIndex: 0, columnIndex: 0 },
      range: { fromRow: 0, toRow: 1, fromColumn: 0, toColumn: 1 },
    });

    stop();

    grid.dispatchEvent(
      new CustomEvent('focuscell', {
        bubbles: true,
        composed: true,
        detail: { focus: { x: 5, y: 5 } },
      }),
    );

    // ⚠ Після відписки — жодного виклику: інакше перехід на іншу таблицю
    // лишав би живого слухача на знятому вузлі.
    expect(report).toHaveBeenCalledTimes(2);

    document.body.removeChild(container);
  });

  it('закріплені колонки: `setrange` бере в\'юпорт зі шляху події, зсув читається щоразу', () => {
    const container = document.createElement('div');
    container.innerHTML =
      '<revogr-viewport-scroll class="colPinStart"><i id="pin"></i></revogr-viewport-scroll>' +
      '<revogr-viewport-scroll class="rgCol"><i id="main"></i></revogr-viewport-scroll>';
    document.body.appendChild(container);
    const pin = container.querySelector('#pin') as HTMLElement;
    const main = container.querySelector('#main') as HTMLElement;

    let pinned = 2;
    const report = vi.fn();
    const stop = trackSelection(container, report, () => pinned);

    const range = { type: 'rgRow', x: 0, y: 3, x1: 1, y1: 4 };
    main.dispatchEvent(new CustomEvent('setrange', { bubbles: true, composed: true, detail: range }));
    expect(report).toHaveBeenLastCalledWith({
      anchor: { rowIndex: 3, columnIndex: 2 },
      range: { fromRow: 3, toRow: 4, fromColumn: 2, toColumn: 3 },
    });

    pin.dispatchEvent(new CustomEvent('setrange', { bubbles: true, composed: true, detail: range }));
    expect(report).toHaveBeenLastCalledWith({
      anchor: { rowIndex: 3, columnIndex: 0 },
      range: { fromRow: 3, toRow: 4, fromColumn: 0, toColumn: 1 },
    });

    // Людина змінила закріплення: наступна подія рахується з новим зсувом.
    pinned = 4;
    main.dispatchEvent(new CustomEvent('setrange', { bubbles: true, composed: true, detail: range }));
    expect(report).toHaveBeenLastCalledWith({
      anchor: { rowIndex: 3, columnIndex: 4 },
      range: { fromRow: 3, toRow: 4, fromColumn: 4, toColumn: 5 },
    });

    stop();
    document.body.removeChild(container);
  });

  it('`setrange` без шляху до в\'юпорту бере в\'юпорт останнього `focuscell`', () => {
    const container = document.createElement('div');
    const report = vi.fn();
    trackSelection(container, report, () => 2);

    container.dispatchEvent(
      new CustomEvent('focuscell', { detail: { rowType: 'rgRow', colType: 'colPinStart', focus: { x: 1, y: 0 } } }),
    );
    container.dispatchEvent(new CustomEvent('setrange', { detail: { type: 'rgRow', x: 0, y: 0, x1: 1, y1: 0 } }));

    expect(report).toHaveBeenLastCalledWith({
      anchor: { rowIndex: 0, columnIndex: 0 },
      range: { fromRow: 0, toRow: 0, fromColumn: 0, toColumn: 1 },
    });
  });

  it('подія без координат не «звалює» виділення в (0,0)', () => {
    const container = document.createElement('div');
    const report = vi.fn();
    trackSelection(container, report);

    container.dispatchEvent(new CustomEvent('focuscell', { detail: {} }));

    expect(report).not.toHaveBeenCalled();
  });
});

describe('focusedCellOfEvent: АКТИВНА комірка, не кут виділення (UI-08)', () => {
  it('віддає саме `focus`, хоч би де був другий кінець діапазону', () => {
    /*
     * ⛔ Це не те саме, що `GridSelection.anchor`, і різниця видима.
     * Виділяючи Shift+↑ знизу вгору, оператор лишає фокус у комірці, з якої
     * почав, тобто в НИЖНЬОМУ кінці; якір — верхній лівий кут. Рядок формули
     * показує те, на чому стоїть курсор, як в Excel, — інакше він підписував
     * би комірку, у якій курсора немає.
     *
     * ⚠ Мутація, яку це ловить: `focusedCellOfEvent = selectionOfFocusEvent(…)
     * ?.anchor` — падає `expected { rowIndex: 4, … } to equal { rowIndex: 7, … }`.
     */
    expect(focusedCellOfEvent({ focus: { x: 3, y: 7 }, end: { x: 1, y: 4 } })).toEqual({
      rowIndex: 7,
      columnIndex: 3,
    });
  });

  it('подія закріпленого рядка (підсумки) і колонок справа ігнорується — індекси там від своєї секції', () => {
    // ⛔ Рядок 0 закріпленої знизу секції — це рядок ПІДСУМКІВ (`UI-08`), а не
    // перший рядок таблиці.
    expect(
      focusedCellOfEvent({ rowType: 'rowPinEnd', colType: 'rgCol', focus: { x: 1, y: 0 } }),
    ).toBeNull();
    expect(
      focusedCellOfEvent({ rowType: 'rgRow', colType: 'colPinEnd', focus: { x: 0, y: 0 } }),
    ).toBeNull();
  });

  it('закріплені колонки: фокус у `colPinStart` -> x, у `rgCol` -> x + кількість закріплених', () => {
    expect(focusedCellOfEvent({ rowType: 'rgRow', colType: 'colPinStart', focus: { x: 1, y: 2 } }, 3)).toEqual({
      rowIndex: 2,
      columnIndex: 1,
    });
    expect(focusedCellOfEvent({ rowType: 'rgRow', colType: 'rgCol', focus: { x: 0, y: 2 } }, 3)).toEqual({
      rowIndex: 2,
      columnIndex: 3,
    });
  });

  it('trackFocusedCell читає зсув закріплення в момент події', () => {
    const container = document.createElement('div');
    let pinned = 1;
    const report = vi.fn();
    trackFocusedCell(container, report, () => pinned);

    const detail = { rowType: 'rgRow', colType: 'rgCol', focus: { x: 2, y: 0 } };
    container.dispatchEvent(new CustomEvent('focuscell', { detail }));
    pinned = 3;
    container.dispatchEvent(new CustomEvent('focuscell', { detail }));

    expect(report).toHaveBeenNthCalledWith(1, { rowIndex: 0, columnIndex: 3 });
    expect(report).toHaveBeenNthCalledWith(2, { rowIndex: 0, columnIndex: 5 });
  });

  it('подія без координат — `null`, а не (0,0)', () => {
    expect(focusedCellOfEvent(undefined)).toBeNull();
    expect(focusedCellOfEvent({})).toBeNull();
    expect(focusedCellOfEvent({ focus: { x: 1 } })).toBeNull();
    expect(focusedCellOfEvent({ focus: { x: -1, y: 0 } })).toBeNull();
  });
});

describe('trackFocusedCell: підписка лише на `focuscell`', () => {
  it('ловить `focuscell` і НЕ реагує на `setrange`', () => {
    /*
     * ⛔ `setrange` координат фокуса не несе взагалі (`{x, y, x1, y1}` — це
     * прямокутник), тож прийняти його кут за активну комірку означало б
     * посунути рядок формули туди, куди курсор не ставав.
     */
    const container = document.createElement('div');
    const grid = document.createElement('div');
    container.appendChild(grid);
    document.body.appendChild(container);

    const report = vi.fn();
    const stop = trackFocusedCell(container, report);

    grid.dispatchEvent(
      new CustomEvent('focuscell', {
        bubbles: true,
        composed: true,
        detail: { rowType: 'rgRow', colType: 'rgCol', focus: { x: 2, y: 5 } },
      }),
    );

    expect(report).toHaveBeenCalledWith({ rowIndex: 5, columnIndex: 2 });

    grid.dispatchEvent(
      new CustomEvent('setrange', {
        bubbles: true,
        composed: true,
        detail: { type: 'rgRow', x: 0, y: 0, x1: 1, y1: 1 },
      }),
    );

    expect(report).toHaveBeenCalledTimes(1);

    stop();

    grid.dispatchEvent(
      new CustomEvent('focuscell', {
        bubbles: true,
        composed: true,
        detail: { focus: { x: 9, y: 9 } },
      }),
    );

    expect(report).toHaveBeenCalledTimes(1);

    document.body.removeChild(container);
  });
});

describe('clampSelection: виділення не переживає зменшення зрізу', () => {
  it('обрізає межі по справжньому розміру', () => {
    expect(clampSelection({ fromRow: 1, toRow: 9, fromColumn: 0, toColumn: 5 }, 3, 2)).toEqual({
      fromRow: 1,
      toRow: 2,
      fromColumn: 0,
      toColumn: 1,
    });
  });

  it('якір за межами зрізу підтягується до останнього рядка/колонки', () => {
    expect(clampSelection({ fromRow: 7, toRow: 7, fromColumn: 7, toColumn: 7 }, 2, 2)).toEqual({
      fromRow: 1,
      toRow: 1,
      fromColumn: 1,
      toColumn: 1,
    });
  });

  it('порожній зріз не має виділення взагалі', () => {
    expect(clampSelection({ fromRow: 0, toRow: 0, fromColumn: 0, toColumn: 0 }, 0, 3)).toBeNull();
    expect(clampSelection({ fromRow: 0, toRow: 0, fromColumn: 0, toColumn: 0 }, 3, 0)).toBeNull();
  });
});
