import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { displayedText, installTruncationTooltip, TruncationTooltipDelayMs } from '../truncationTooltip';

/**
 * Підказка повного тексту обрізаних заголовків/груп/комірок сітки документа (`truncationTooltip.ts`).
 *
 * jsdom не має розкладки, тож «обрізаність» задається явно: `scrollWidth`/`clientWidth` елемента. Перевіряється
 * поведінка (показ лише при обрізанні, фокус, Escape/прокрутка/вихід) і продуктивність: фіксований набір
 * слухачів на корені незалежно від кількості комірок, і жодного читання розмітки до моменту показу.
 *
 * Мутації (усі дають червоне): прибрати перевірку `isTruncated` у `pick` — «текст уміщається» показує підказку;
 * поставити слухач на кожну комірку — «5 000 комірок» рахує зайві `addEventListener`; читати `scrollWidth` у
 * `onPointerOver` — «жодного читання розмітки» ловить читання до затримки.
 */

let root: HTMLElement;
let cleanup: (() => void) | null = null;

function size(el: HTMLElement, scrollWidth: number, clientWidth: number): HTMLElement {
  Object.defineProperty(el, 'scrollWidth', { configurable: true, get: () => scrollWidth });
  Object.defineProperty(el, 'clientWidth', { configurable: true, get: () => clientWidth });

  return el;
}

function cell(text: string, className = 'rgCell'): HTMLElement {
  const el = document.createElement('div');
  el.className = className;
  el.tabIndex = -1;
  el.textContent = text;
  root.appendChild(el);

  return el;
}

const tip = (): HTMLElement | null => document.querySelector<HTMLElement>('[data-truncation-tooltip]');
const visibleTip = (): HTMLElement | null => {
  const node = tip();

  return node !== null && !node.hidden ? node : null;
};

function hover(target: Element, from: Element | null = null): void {
  target.dispatchEvent(new MouseEvent('pointerover', { bubbles: true, relatedTarget: from }));
}

function leave(target: Element, to: Element | null): void {
  target.dispatchEvent(new MouseEvent('pointerout', { bubbles: true, relatedTarget: to }));
}

beforeEach(() => {
  vi.useFakeTimers();
  root = document.createElement('div');
  document.body.appendChild(root);
});

afterEach(() => {
  cleanup?.();
  cleanup = null;
  root.remove();
  vi.useRealTimers();
  vi.restoreAllMocks();
});

describe('truncationTooltip · показ лише для обрізаного тексту', () => {
  it('обрізаний заголовок: після затримки — підказка role=tooltip з повним текстом', () => {
    const header = size(cell('Description of equipment', 'rgHeaderCell'), 220, 90);
    cleanup = installTruncationTooltip(root);

    hover(header);
    expect(visibleTip()).toBeNull();
    vi.advanceTimersByTime(TruncationTooltipDelayMs - 1);
    expect(visibleTip()).toBeNull();
    vi.advanceTimersByTime(1);

    expect(visibleTip()?.textContent).toBe('Description of equipment');
    expect(tip()?.getAttribute('role')).toBe('tooltip');
    expect(tip()?.hasAttribute('tabindex')).toBe(false);
  });

  it('текст уміщається — підказки немає', () => {
    const header = size(cell('Rot', 'rgHeaderCell'), 30, 90);
    cleanup = installTruncationTooltip(root);

    hover(header);
    vi.advanceTimersByTime(TruncationTooltipDelayMs * 2);

    expect(visibleTip()).toBeNull();
  });

  it('порожній текст — підказки немає, навіть коли елемент «переповнений»', () => {
    const empty = size(cell('   '), 200, 50);
    cleanup = installTruncationTooltip(root);

    hover(empty);
    vi.advanceTimersByTime(TruncationTooltipDelayMs);

    expect(visibleTip()).toBeNull();
  });

  it('обрізаний внутрішній елемент (назва в двоярусному заголовку) — текст саме його, без підпису читалки', () => {
    const header = size(cell('', 'rgHeaderCell'), 90, 90);
    const name = document.createElement('span');
    name.className = 'ecr-header-name';
    name.innerHTML = '<span>Status of the rotating equipment</span><span class="ecr-header-sr">, </span><span>Submitted</span>';
    header.appendChild(name);
    size(name, 300, 80);
    cleanup = installTruncationTooltip(root);

    hover(name.firstElementChild as Element);
    vi.advanceTimersByTime(TruncationTooltipDelayMs);

    expect(visibleTip()?.textContent).toBe('Status of the rotating equipment, Submitted');
  });

  it('групова смуга: шлях групи для читалки й кнопки до тексту не входять', () => {
    const group = cell('', 'rgHeaderCell');
    group.innerHTML =
      '<span class="ecr-hg-leaf"><span class="ecr-header-sr">Pollutants / Gases: </span>Nitrogen oxides</span><button type="button">⋮</button>';

    expect(displayedText(group)).toBe('Nitrogen oxides');
  });

  it('заголовок зберігає aria-label, а title повертається після приховування', () => {
    const header = size(cell('Stationary combustion sources', 'rgHeaderCell'), 400, 60);
    header.setAttribute('aria-label', 'Stationary combustion sources');
    header.setAttribute('title', 'Required column');
    cleanup = installTruncationTooltip(root);

    hover(header);
    vi.advanceTimersByTime(TruncationTooltipDelayMs);

    // Поки видно нашу — браузерної другої немає, а текст `title` став приглушеним другим рядком.
    expect(header.hasAttribute('title')).toBe(false);
    expect(visibleTip()?.querySelector('.ecr-trunc-tip-extra')?.textContent).toBe('Required column');
    expect(header.getAttribute('aria-label')).toBe('Stationary combustion sources');

    leave(header, document.body);
    expect(visibleTip()).toBeNull();
    expect(header.getAttribute('title')).toBe('Required column');
    expect(header.getAttribute('aria-label')).toBe('Stationary combustion sources');
  });
});

describe('truncationTooltip · клавіатура і приховування', () => {
  it('фокус на обрізаній комірці — підказка одразу, без затримки; blur ховає', () => {
    const lookup = size(cell('Gas turbine GT-101 (main compressor station)'), 400, 100);
    const other = cell('1');
    cleanup = installTruncationTooltip(root);

    lookup.focus();
    expect(visibleTip()?.textContent).toBe('Gas turbine GT-101 (main compressor station)');

    other.focus();
    expect(visibleTip()).toBeNull();
  });

  it('фокус одразу після натискання миші — не клавіатурний: підказку не показує', () => {
    const value = size(cell('Very long value of the cell'), 400, 100);
    cleanup = installTruncationTooltip(root);

    value.dispatchEvent(new MouseEvent('pointerdown', { bubbles: true }));
    value.focus();

    expect(visibleTip()).toBeNull();
  });

  it('підказку з клавіатури не ховає pointerover нерухомої миші (перемальована під курсором розмітка); справжній рух — ховає', () => {
    // ⛔ Мутація: прибрати перевірку `pointerMoved` в `onPointerOver` — червоне (e2e gridTooltip.spec.ts: стрілка після
    // кліку, підказка зникала за ~3 мс від `pointerover` на `header-content` під нерухомим курсором).
    const clicked = cell('C1');
    const value = size(cell('Very long value of the cell'), 400, 100);
    const headerContent = document.createElement('div');
    headerContent.className = 'header-content';
    root.appendChild(headerContent);
    cleanup = installTruncationTooltip(root);

    const at = { clientX: 120, clientY: 64 };
    clicked.dispatchEvent(new MouseEvent('pointerover', { bubbles: true, ...at }));
    clicked.dispatchEvent(new MouseEvent('pointerdown', { bubbles: true, ...at }));
    vi.advanceTimersByTime(600);

    value.focus();
    expect(visibleTip()?.textContent).toBe('Very long value of the cell');

    // Курсор на місці, під ним змінилася розмітка — браузер шле boundary-події з тими самими координатами.
    clicked.dispatchEvent(new MouseEvent('pointerout', { bubbles: true, relatedTarget: headerContent, ...at }));
    headerContent.dispatchEvent(new MouseEvent('pointerover', { bubbles: true, relatedTarget: clicked, ...at }));
    vi.advanceTimersByTime(TruncationTooltipDelayMs * 2);
    expect(visibleTip()?.textContent).toBe('Very long value of the cell');

    // Миша справді рушила — підказка фокуса поступається наведенню.
    headerContent.dispatchEvent(new MouseEvent('pointerover', { bubbles: true, clientX: 300, clientY: 64 }));
    expect(visibleTip()).toBeNull();
  });

  it('Escape ховає підказку', () => {
    const value = size(cell('Very long value of the cell'), 400, 100);
    cleanup = installTruncationTooltip(root);

    value.focus();
    expect(visibleTip()).not.toBeNull();
    value.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));

    expect(visibleTip()).toBeNull();
  });

  it('прокрутка всередині сітки ховає підказку', () => {
    const value = size(cell('Very long value of the cell'), 400, 100);
    cleanup = installTruncationTooltip(root);

    value.focus();
    expect(visibleTip()).not.toBeNull();
    value.dispatchEvent(new Event('scroll'));

    expect(visibleTip()).toBeNull();
  });

  it('вихід миші до показу скасовує його; вихід після показу ховає; перехід на саму підказку — тримає', () => {
    const value = size(cell('Very long value of the cell'), 400, 100);
    const neighbour = cell('x');
    cleanup = installTruncationTooltip(root);

    hover(value);
    leave(value, neighbour);
    vi.advanceTimersByTime(TruncationTooltipDelayMs * 2);
    expect(visibleTip()).toBeNull();

    hover(value);
    vi.advanceTimersByTime(TruncationTooltipDelayMs);
    const shown = visibleTip();
    expect(shown).not.toBeNull();

    leave(value, shown);
    expect(visibleTip()).not.toBeNull();

    shown?.dispatchEvent(new MouseEvent('pointerleave', { relatedTarget: neighbour }));
    expect(visibleTip()).toBeNull();
  });

  it('позиція — у межах вікна (зсув від правого краю)', () => {
    const value = size(cell('Very long value of the cell'), 400, 100);
    vi.spyOn(value, 'getBoundingClientRect').mockReturnValue(
      { left: window.innerWidth - 20, right: window.innerWidth, top: 100, bottom: 128, width: 20, height: 28, x: 0, y: 0, toJSON: () => ({}) } as DOMRect,
    );
    cleanup = installTruncationTooltip(root);

    value.focus();
    const left = Number.parseFloat(visibleTip()?.style.left ?? 'NaN');

    expect(left).toBeLessThanOrEqual(window.innerWidth - 8);
    expect(left).toBeGreaterThanOrEqual(8);
    expect(visibleTip()?.style.top).toBe('128px');
  });

  it('зняття прибирає вузол підказки', () => {
    const value = size(cell('Very long value of the cell'), 400, 100);
    cleanup = installTruncationTooltip(root);
    value.focus();
    expect(tip()).not.toBeNull();

    cleanup();
    cleanup = null;

    expect(tip()).toBeNull();
  });
});

describe('truncationTooltip · продуктивність (5 000 комірок)', () => {
  const CellCount = 5_000;

  it('фіксований набір слухачів на корені, жодного на комірці', () => {
    for (let i = 0; i < CellCount; i += 1) cell(`Value ${String(i)}`);
    const add = vi.spyOn(EventTarget.prototype, 'addEventListener');

    cleanup = installTruncationTooltip(root);

    const onRoot = add.mock.contexts.filter((context) => context === root).length;
    const onCells = add.mock.contexts.filter((context) => context instanceof Element && context !== root && root.contains(context)).length;
    // pointerover, pointerout, pointerdown, focusin, focusout, scroll.
    expect(onRoot).toBe(6);
    expect(onCells).toBe(0);
  });

  it('жодного читання розмітки під час прокрутки й руху миші — лише при показі, лише для однієї комірки', () => {
    const cells = Array.from({ length: CellCount }, (_, i) => cell(`Value ${String(i)}`));
    const reads: Element[] = [];
    // Перекриття на `HTMLElement.prototype` (власного дескриптора там немає — jsdom тримає його на `Element`).
    const proto = HTMLElement.prototype;
    const rect = vi.spyOn(Element.prototype, 'getBoundingClientRect');
    Object.defineProperty(proto, 'scrollWidth', {
      configurable: true,
      get(this: Element) {
        reads.push(this);
        return 500;
      },
    });
    Object.defineProperty(proto, 'clientWidth', {
      configurable: true,
      get(this: Element) {
        reads.push(this);
        return 100;
      },
    });

    try {
      cleanup = installTruncationTooltip(root);
      // Миша проходить над усіма комірками швидше за затримку, сітка прокручується.
      for (let i = 1; i < cells.length; i += 1) hover(cells[i] as Element, cells[i - 1] as Element);
      root.dispatchEvent(new Event('scroll'));
      expect(reads).toHaveLength(0);
      expect(rect).not.toHaveBeenCalled();

      // Мишу зупинили на одній комірці: читання — лише її.
      const last = cells[cells.length - 1] as Element;
      hover(last);
      vi.advanceTimersByTime(TruncationTooltipDelayMs);
      expect(visibleTip()).not.toBeNull();
      expect(new Set(reads)).toEqual(new Set([last]));
      expect(rect.mock.contexts).toEqual([last]);
    } finally {
      delete (proto as unknown as Record<string, unknown>).scrollWidth;
      delete (proto as unknown as Record<string, unknown>).clientWidth;
    }
  });
});
