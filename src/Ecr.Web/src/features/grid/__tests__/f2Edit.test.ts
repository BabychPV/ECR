import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { installF2Edit, isOpenEditorF2 } from '../f2Edit';
import { installKeyCommitGate } from '../keyCommitGate';

/**
 * A2: F2 у сітці документа відкриває редактор поточної комірки.
 *
 * ⚠ Справжній RevoGrid у jsdom не монтується, тож тут перевіряється контракт `f2Edit.ts`: F2
 * поза редактором ДО черги вводу (`document`, capture) і до RevoGrid доходить як Enter (той
 * самий шлях відкриття і та сама перевірка права `canEdit()`), а поле відкритого після F2
 * редактора отримує курсор у кінці. Живий RevoGrid - `e2e/gridF2Live.spec.ts`.
 */
let container: HTMLElement;
let cell: HTMLElement;
let cleanup: (() => void)[] = [];
let seen: string[] = [];

const recordKeys = (event: KeyboardEvent): void => {
  seen.push(event.key);
};

function press(target: EventTarget, init: KeyboardEventInit): KeyboardEvent {
  const event = new KeyboardEvent('keydown', { bubbles: true, cancelable: true, ...init });
  target.dispatchEvent(event);

  return event;
}

/** Як RevoGrid: редактор - `<input>` в обгортці `.edit-input-wrapper`. */
function openEditor(value: string): HTMLInputElement {
  const wrapper = document.createElement('div');
  wrapper.className = 'edit-input-wrapper';
  const input = document.createElement('input');
  input.type = 'text';
  input.value = value;
  input.setSelectionRange(0, 0);
  wrapper.appendChild(input);
  container.appendChild(wrapper);

  return input;
}

beforeEach(() => {
  container = document.createElement('div');
  cell = document.createElement('div');
  cell.tabIndex = 0;
  container.appendChild(cell);
  document.body.appendChild(container);
  seen = [];
  // Читач після `f2Edit` - як черга вводу і RevoGrid (обидва слухають `document`).
  document.addEventListener('keydown', recordKeys, true);
  cleanup = [installF2Edit(container), () => document.removeEventListener('keydown', recordKeys, true)];
});

afterEach(() => {
  for (const dispose of cleanup) dispose();
  container.remove();
});

describe('F2 у сітці документа (A2)', () => {
  it('F2 на комірці доходить до черги вводу і RevoGrid як Enter', () => {
    press(cell, { key: 'F2', code: 'F2' });

    expect(seen).toEqual(['Enter']);
  });

  it('інші клавіші не чіпає', () => {
    press(cell, { key: 'F3' });
    press(cell, { key: 'Enter' });
    press(cell, { key: '7' });

    expect(seen).toEqual(['F3', 'Enter', '7']);
  });

  it('F2 з модифікатором (Shift/Ctrl/Alt) - не відкриття', () => {
    press(cell, { key: 'F2', shiftKey: true });
    press(cell, { key: 'F2', ctrlKey: true });
    press(cell, { key: 'F2', altKey: true });

    expect(seen).toEqual(['F2', 'F2', 'F2']);
  });

  it('F2 поза сіткою не чіпає', () => {
    const outside = document.createElement('button');
    document.body.appendChild(outside);
    press(outside, { key: 'F2' });
    outside.remove();

    expect(seen).toEqual(['F2']);
  });

  it('F2 у полі відкритого редактора лишається F2 (Enter зберіг би значення)', () => {
    const input = openEditor('42');
    press(input, { key: 'F2' });

    expect(seen).toEqual(['F2']);
  });

  it('F2, поки редактор відкритий, але фокус ще не в полі - не Enter', () => {
    openEditor('42');
    press(cell, { key: 'F2' });

    expect(seen).toEqual(['F2']);
  });

  it('після F2 поле редактора отримує курсор у кінці значення', () => {
    press(cell, { key: 'F2' });
    const input = openEditor('12.5');
    input.focus();

    expect([input.selectionStart, input.selectionEnd]).toEqual([4, 4]);
  });

  it('без F2 (відкриття Enter/кліком) курсор не переставляє', () => {
    const input = openEditor('12.5');
    input.focus();

    expect([input.selectionStart, input.selectionEnd]).toEqual([0, 0]);
  });

  it('isOpenEditorF2: лише F2 без модифікаторів і поза редактором', () => {
    expect(isOpenEditorF2(new KeyboardEvent('keydown', { key: 'F2' }))).toBe(true);
    expect(isOpenEditorF2(new KeyboardEvent('keydown', { key: 'F2', metaKey: true }))).toBe(false);
    expect(isOpenEditorF2(new KeyboardEvent('keydown', { key: 'Enter' }))).toBe(false);
  });
});

describe('F2 і черга вводу (T3-01/A1-03)', () => {
  it('символ одразу після F2 чекає в черзі, як після Enter, - не губиться', () => {
    cleanup.push(installKeyCommitGate(container));
    cell.focus();

    press(cell, { key: 'F2' });
    const typed = press(cell, { key: '5' });

    // Черга тримає вікно «відкриття»: символ відкладено до фокуса поля редактора.
    expect(typed.defaultPrevented).toBe(true);
  });

  it('контроль: без F2 символ не затримується чергою як «після відкриття»', () => {
    cleanup.push(installKeyCommitGate(container));
    cell.focus();

    press(cell, { key: 'F3' });
    const typed = press(cell, { key: '5' });

    // '5' сам відкриває редактор (символ) - сам він не відкладається.
    expect(typed.defaultPrevented).toBe(false);
  });
});
