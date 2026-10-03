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

function mountGrid(rows: number, startRow: number, withGate: boolean): Grid {
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
        editor.parentElement?.remove();
        editor = null;
        setTimeout(() => {
          row = Math.min(row + 1, rows - 1);
          holder.focus();
          container.dispatchEvent(new CustomEvent('focuscell', { bubbles: true }));
        }, FocusMoveMs);
      }

      return;
    }

    if (event.key === 'Enter') open(values[row] ?? '');
    else if (event.key.length === 1) open(event.key);
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

  it('повільний ввід (пауза ≥ 0,1 с) поводиться як раніше', () => {
    const grid = mountGrid(4, 1, true);

    type(grid, keys, 150);

    expect(grid.values).toEqual(['', '1', '2', '']);
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
