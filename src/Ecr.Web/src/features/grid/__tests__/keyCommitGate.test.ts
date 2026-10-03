import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { installKeyCommitGate } from '../keyCommitGate';

/**
 * T3-01 (P2): швидка послідовність `Enter, 1, Enter, Enter, 2, Enter` без пауз
 * склеювала значення й писала їх не в ту комірку.
 *
 * ⚠ Справжній RevoGrid у jsdom не монтується (див. `keyboardCompat.test.ts`),
 * тому тут — МОДЕЛЬ його ЧАСОВОЇ поведінки, виміряної в Chromium
 * (`@revolist/revogrid` 4.11, `revogr-overlay-selection` + `keyboard.service`):
 *  - Enter у редакторі зберігає значення й закриває редактор (blur) одразу;
 *  - фокус на наступний рядок іде через ~70 мс (`timeout(RESIZE_INTERVAL + 30)`),
 *    після чого летить `focuscell`;
 *  - Enter/символ ПОЗА редактором відкриває редактор на поточній комірці, з її
 *    значенням; `<input>` отримує фокус лише наступним тіком (`await timeout()`).
 * Саму модель підтверджено живим прогоном у браузері: без черги значення
 * губляться чи потрапляють не в ту комірку, з чергою — кожне у свою.
 */
const FocusMoveMs = 70;

interface Grid {
  container: HTMLElement;
  values: string[];
  press: (key: string) => void;
  dispose: () => void;
}

function mountGrid(rows: number, startRow: number, withGate: boolean, lingeringEditor = false): Grid {
  const container = document.createElement('div');
  const overlay = document.createElement('revogr-overlay-selection');
  const holder = document.createElement('div');
  holder.tabIndex = 0;
  overlay.appendChild(holder);
  container.appendChild(overlay);
  document.body.appendChild(container);

  const values = Array.from({ length: rows }, () => '');
  let row = startRow;
  let editor: HTMLInputElement | null = null;

  const open = (initial: string): void => {
    const wrapper = document.createElement('div');
    wrapper.className = 'edit-input-wrapper';
    const input = document.createElement('input');
    input.value = initial;
    wrapper.appendChild(input);
    overlay.appendChild(wrapper);
    editor = input;
    setTimeout(() => input.focus(), 0);
  };

  overlay.addEventListener('keydown', (event) => {
    if (editor !== null) {
      if (event.key === 'Enter') {
        values[row] = editor.value;
        editor.blur();
        const closing = editor;
        // ⚠ T4-01: справжній RevoGrid лишає старий редактор у DOM до самого
        // переходу фокуса й прибирає його ПІСЛЯ `focuscell` (кілька мс).
        if (!lingeringEditor) {
          closing.parentElement?.remove();
          editor = null;
        }

        setTimeout(() => {
          row = Math.min(row + 1, rows - 1);
          holder.focus();
          container.dispatchEvent(new CustomEvent('focuscell', { bubbles: true }));
          if (lingeringEditor) {
            setTimeout(() => {
              closing.parentElement?.remove();
              editor = null;
            }, 2);
          }
        }, FocusMoveMs);
      }

      return;
    }

    if (event.key === 'Enter') open(values[row] ?? '');
    // ⚠ RevoGrid не відкриває редактор від ярлика (Ctrl/Alt + символ).
    else if (event.key.length === 1 && !event.ctrlKey && !event.altKey) open(event.key);
  });

  holder.focus();
  const dispose = withGate ? installKeyCommitGate(container) : () => undefined;

  const press = (key: string): void => {
    const target: EventTarget = document.activeElement ?? document.body;
    const event = new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true });
    target.dispatchEvent(event);

    // Звичайна клавіша друкує символ у сфокусований `<input>` — як браузер.
    if (!event.defaultPrevented && target instanceof HTMLInputElement && key.length === 1) {
      target.value += key;
    }
  };

  return { container, values, press, dispose };
}

/** Надсилає клавіші з паузою `gapMs` між ними й дочікується завершення переходів. */
function type(grid: Grid, keys: string[], gapMs: number): void {
  for (const key of keys) {
    grid.press(key);
    vi.advanceTimersByTime(gapMs);
  }

  vi.advanceTimersByTime(2000);
}

describe('installKeyCommitGate: швидкий ввід не склеює значення (T3-01)', () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.useRealTimers();
    document.body.innerHTML = '';
  });

  const keys = ['Enter', '1', 'Enter', 'Enter', '2', 'Enter'];

  it('без пауз: кожне значення в СВОЇЙ комірці, у порядку введення', () => {
    const grid = mountGrid(4, 1, true);

    type(grid, keys, 0);

    expect(grid.values).toEqual(['', '1', '2', '']);
    grid.dispose();
  });

  it('три значення підряд з паузою 3 мс (темп сканера): жодне не губиться', () => {
    const grid = mountGrid(5, 1, true);

    type(grid, ['Enter', 'a', 'Enter', 'Enter', 'b', 'Enter', 'Enter', 'c', 'Enter'], 3);

    expect(grid.values).toEqual(['', 'a', 'b', 'c', '']);
    grid.dispose();
  });

  it('T4-01: редактор лишається в DOM після focuscell - інтервали 0..120 мс дають значення у СВОЇХ рядках', () => {
    for (const gap of [0, 10, 30, 50, 60, 65, 70, 80, 100, 120]) {
      const grid = mountGrid(5, 1, true, true);

      type(grid, keys, gap);

      expect(grid.values, `gap ${gap}`).toEqual(['', '1', '2', '', '']);
      grid.dispose();
      document.body.innerHTML = '';
    }
  });

  it('повільний ввід (пауза ≥ 0,1 с) поводиться як раніше', () => {
    const grid = mountGrid(4, 1, true);

    type(grid, keys, 150);

    expect(grid.values).toEqual(['', '1', '2', '']);
    grid.dispose();
  });

  /** Відкриває вікно затримки: Enter, символ, Enter (коміт) — без часу на завершення. */
  function openCommitWindow(grid: Grid): void {
    grid.press('Enter');
    vi.advanceTimersByTime(5);
    grid.press('1');
    grid.press('Enter');
  }

  /** keydown на `target`; повертає, чи дійшов він до слухача на `document` одразу. */
  function fire(target: EventTarget, init: KeyboardEventInit): { delivered: boolean; prevented: boolean } {
    let delivered = false;
    const listener = (): void => {
      delivered = true;
    };
    document.addEventListener('keydown', listener);
    const event = new KeyboardEvent('keydown', { bubbles: true, cancelable: true, ...init });
    target.dispatchEvent(event);
    document.removeEventListener('keydown', listener);

    return { delivered, prevented: event.defaultPrevented };
  }

  it('M2: клавіша в полі ПОЗА гридом (input/textarea/select/модалка) у вікні затримки не затримується', () => {
    const grid = mountGrid(4, 1, true);
    const modal = document.createElement('div');
    modal.setAttribute('role', 'dialog');
    const outside = [
      document.createElement('input'),
      document.createElement('textarea'),
      document.createElement('select'),
      modal,
    ];
    for (const element of outside) document.body.appendChild(element);

    openCommitWindow(grid);

    for (const element of outside) {
      const result = fire(element, { key: 'x' });
      expect(result.delivered, element.tagName).toBe(true);
      expect(result.prevented, element.tagName).toBe(false);
    }

    // І нічого з цього не відтворюється пізніше в грид.
    vi.advanceTimersByTime(2000);
    expect(grid.values).toEqual(['', '1', '', '']);
    grid.dispose();
  });

  it('M3: Ctrl/Meta/Alt+клавіша (Ctrl+C/V/Z/S) у вікні затримки не затримується й не переупорядковується', () => {
    const grid = mountGrid(4, 1, true);
    const holder = grid.container.querySelector('div[tabindex]') ?? document.body;

    openCommitWindow(grid);

    const combos: KeyboardEventInit[] = [
      { key: 'c', ctrlKey: true },
      { key: 'v', metaKey: true },
      { key: 'z', ctrlKey: true },
      { key: 's', ctrlKey: true },
      { key: 'x', altKey: true },
    ];
    for (const init of combos) {
      for (const target of [holder, document.body]) {
        const result = fire(target, init);
        expect(result.delivered, `${init.key} -> ${target instanceof Element ? target.tagName : ''}`).toBe(true);
        expect(result.prevented).toBe(false);
      }
    }

    // Звичайна клавіша в тому ж вікні, як і раніше, у черзі.
    expect(fire(document.body, { key: 'q' }).delivered).toBe(false);
    grid.dispose();
  });

  it('T4-07: AltGr-символ (ctrl+alt або AltGraph) одразу після Enter ставиться в чергу й не губиться', () => {
    const variants: KeyboardEventInit[] = [
      { key: '@', code: 'Digit2', ctrlKey: true, altKey: true },
      { key: '@', code: 'Digit2', modifierAltGraph: true },
    ];

    for (const init of variants) {
      const grid = mountGrid(4, 1, true);

      openCommitWindow(grid);
      const result = fire(document.body, init);
      grid.press('Enter');
      vi.advanceTimersByTime(2000);

      expect(result.delivered, JSON.stringify(init)).toBe(false);
      expect(grid.values, JSON.stringify(init)).toEqual(['', '1', '@', '']);
      grid.dispose();
      document.body.innerHTML = '';
    }
  });

  it('T4-07: Ctrl+Alt+літера, що збігається з кодом клавіші (ярлик), не затримується', () => {
    const grid = mountGrid(4, 1, true);

    openCommitWindow(grid);

    const result = fire(document.body, { key: 'q', code: 'KeyQ', ctrlKey: true, altKey: true });

    expect(result.delivered).toBe(true);
    expect(result.prevented).toBe(false);
    grid.dispose();
  });

  it('контроль моделі: БЕЗ черги та сама послідовність псує дані', () => {
    // Доказ, що тест відтворює дефект, а не проходить за будь-яких умов.
    const grid = mountGrid(4, 1, false);

    type(grid, keys, 0);

    expect(grid.values).not.toEqual(['', '1', '2', '']);
  });

  it('останній рядок: фокус нікуди не переходить — клавіші все одно відтворюються після запасного терміну', () => {
    const grid = mountGrid(2, 1, true);

    type(grid, ['Enter', '7', 'Enter'], 0);

    expect(grid.values).toEqual(['', '7']);
    grid.dispose();
  });
});
