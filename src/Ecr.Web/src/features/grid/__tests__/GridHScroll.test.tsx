import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, render, screen } from '@testing-library/react';
import { GridHScroll, GridScrollSelector, mapScroll, spacerWidth } from '../GridHScroll';

/**
 * ✎ ui-sheet-bottom: горизонтальна прокрутка таблиці в нижньому рядку — нативна смуга, синхронізована зі
 * штатним скролом RevoGrid (`revogr-scroll-virtual.horizontal`). У jsdom розкладки немає, тож розміри й
 * `scrollLeft` підміняються на прототипі за класом елемента.
 */
const store = new WeakMap<Element, number>();
const sizes = new WeakMap<Element, { client: number; scroll: number }>();

function install(): () => void {
  const originals = ['scrollLeft', 'scrollWidth', 'clientWidth'].map((name) => ({
    name,
    descriptor: Object.getOwnPropertyDescriptor(Element.prototype, name),
  }));

  Object.defineProperty(Element.prototype, 'scrollLeft', {
    configurable: true,
    get(this: Element) {
      return store.get(this) ?? 0;
    },
    set(this: Element, value: number) {
      store.set(this, value);
    },
  });
  Object.defineProperty(Element.prototype, 'clientWidth', {
    configurable: true,
    get(this: Element) {
      if (this.classList.contains('ecr-hscroll')) return 200;
      return sizes.get(this)?.client ?? 0;
    },
  });
  Object.defineProperty(Element.prototype, 'scrollWidth', {
    configurable: true,
    get(this: Element) {
      if (this.classList.contains('ecr-hscroll')) {
        const inner = this.firstElementChild as HTMLElement | null;
        return Math.max(200, Number.parseFloat(inner?.style.width ?? '0') || 0);
      }
      return sizes.get(this)?.scroll ?? 0;
    },
  });

  return () => {
    for (const { name, descriptor } of originals) {
      if (descriptor !== undefined) Object.defineProperty(Element.prototype, name, descriptor);
      else Reflect.deleteProperty(Element.prototype, name);
    }
  };
}

let restore: () => void;
let host: HTMLElement;
let source: HTMLElement;

beforeEach(() => {
  restore = install();
  host = document.createElement('div');
  source = document.createElement('revogr-scroll-virtual');
  source.className = 'horizontal';
  source.appendChild(document.createElement('div'));
  host.appendChild(source);
  document.body.appendChild(host);
  sizes.set(source, { client: 1000, scroll: 3000 });
});

afterEach(() => {
  host.remove();
  restore();
});

function show(): HTMLElement {
  render(<GridHScroll resolveHost={() => host} container={() => host} watch="t1" />);
  return screen.getByTestId('grid-hscroll');
}

describe('GridHScroll: чисті функції', () => {
  it('spacerWidth: частка повзунка збігається з часткою сітки', () => {
    expect(spacerWidth(200, 1000, 3000)).toBe(600);
    expect(spacerWidth(200, 1000, 1000)).toBe(0);
    expect(spacerWidth(0, 1000, 3000)).toBe(0);
  });

  it('mapScroll: пропорційно діапазонам, у межах [0, range]', () => {
    expect(mapScroll(1000, 2000, 400)).toBe(200);
    expect(mapScroll(5000, 2000, 400)).toBe(400);
    expect(mapScroll(-5, 2000, 400)).toBe(0);
    expect(mapScroll(10, 0, 400)).toBe(0);
  });
});

describe('GridHScroll: синхронізація scrollLeft', () => {
  it('сітка прокручена → смуга показує пропорційну позицію; ехо смуги сітку не рухає', () => {
    const track = show();
    expect(track.getAttribute('data-scrollable')).toBe('true');
    expect((track.firstElementChild as HTMLElement).style.width).toBe('600px');

    act(() => {
      source.scrollLeft = 1000;
      source.dispatchEvent(new Event('scroll'));
    });
    expect(track.scrollLeft).toBe(200);

    // Браузер повертає подію scroll і для програмного виставлення смуги — сітка лишається де була.
    act(() => {
      track.dispatchEvent(new Event('scroll'));
    });
    expect(source.scrollLeft).toBe(1000);
  });

  it('смугу потягнули → сітка прокручується пропорційно', () => {
    const track = show();

    act(() => {
      track.scrollLeft = 100;
      track.dispatchEvent(new Event('scroll'));
    });
    expect(source.scrollLeft).toBe(500);
  });

  it('прокручувати нічого — смуга позначена нескролабельною і не бере фокус', () => {
    sizes.set(source, { client: 1000, scroll: 1000 });
    const track = show();

    expect(track.getAttribute('data-scrollable')).toBe('false');
    expect(track.tabIndex).toBe(-1);
  });

  it('сітка з’являється пізніше (веб-компонент) — смуга підключається, щойно вона є', async () => {
    host.removeChild(source);
    const track = show();
    expect(track.getAttribute('data-scrollable')).toBe('false');

    await act(async () => {
      host.appendChild(source);
      await Promise.resolve();
    });
    expect(track.getAttribute('data-scrollable')).toBe('true');
  });

  it('селектор штатного скролу — той, що в RevoGrid', () => {
    expect(GridScrollSelector).toBe('revogr-scroll-virtual.horizontal');
    expect(host.querySelector(GridScrollSelector)).toBe(source);
  });
  it('після потягнутої смуги подію scroll на сітці повторено (RevoGrid відкидає ранню, < 10 мс після колеса)', () => {
    vi.useFakeTimers();
    const track = show();
    let seen = 0;
    source.addEventListener('scroll', () => {
      seen += 1;
    });

    act(() => {
      track.scrollLeft = 100;
      track.dispatchEvent(new Event('scroll'));
    });
    expect(seen).toBe(0);

    act(() => {
      vi.advanceTimersByTime(50);
    });
    expect(seen).toBe(1);
    expect(source.scrollLeft).toBe(500);
    vi.useRealTimers();
  });
});
