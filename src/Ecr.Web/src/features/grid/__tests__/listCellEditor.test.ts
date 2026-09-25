import { afterEach, describe, expect, it, vi } from 'vitest';
import { h } from '@revolist/revogrid';
import type { ColumnDataSchemaModel, EditCell } from '@revolist/revogrid';
import {
  createListCellEditor,
  filterOptions,
  MaxShownOptions,
  mountListEditor,
  type ListOption,
} from '../listCellEditor';

/**
 * Редактор-список комірки (`X-13`, `R-01`): стрілки ПЕРЕМІЩУЮТЬ, Enter і клік
 * ПІДТВЕРДЖУЮТЬ, друк ШУКАЄ.
 *
 * ⛔ Що ламалося, живцем на стенді: редактори `Lookup`/`Bool` були голим
 * `<select>` зі збереженням на `onChange`. Фокус у нього не переходив
 * (`autoFocus` у VNode RevoGrid не спрацьовує), а перша ж стрілка вниз
 * змінювала значення — і `onChange` зберігав його й закривав редактор. Тобто
 * «подивитись варіанти» означало «переписати комірку сусіднім записом».
 */

const Options: readonly ListOption[] = [
  { value: null, label: '(clear)' },
  { value: '1', label: 'Natural gas', hint: 'GAS' },
  { value: '2', label: 'Diesel fuel', hint: 'DSL' },
  { value: '3', label: 'Fuel oil', hint: 'MZT' },
];

function host(): HTMLDivElement {
  const node = document.createElement('div');
  document.body.append(node);

  return node;
}

function key(input: HTMLInputElement, name: string): KeyboardEvent {
  const event = new KeyboardEvent('keydown', { key: name, bubbles: true, cancelable: true });
  input.dispatchEvent(event);

  return event;
}

function type(input: HTMLInputElement, text: string): void {
  input.value = text;
  input.dispatchEvent(new Event('input', { bubbles: true }));
}

function activeLabel(_host: HTMLElement): string | null | undefined {
  return document.querySelector('[role="option"][aria-selected="true"]')?.firstElementChild?.textContent;
}

afterEach(() => {
  document.body.replaceChildren();
  vi.useRealTimers();
});

describe('mountListEditor — клавіатура', () => {
  it('стрілка вниз лише переміщує виділення і НІЧОГО не зберігає', () => {
    const onCommit = vi.fn();
    const node = host();
    const { input } = mountListEditor(node, {
      options: Options, selected: '1', initialQuery: '', ariaLabel: 'Fuel', onCommit,
    });

    // Починаємо з поточного значення комірки.
    expect(activeLabel(node)).toBe('Natural gas');

    const event = key(input, 'ArrowDown');

    expect(event.defaultPrevented).toBe(true);
    expect(activeLabel(node)).toBe('Diesel fuel');
    expect(onCommit).not.toHaveBeenCalled();

    key(input, 'ArrowUp');
    key(input, 'ArrowUp');
    expect(activeLabel(node)).toBe('(clear)');
    expect(onCommit).not.toHaveBeenCalled();
  });

  it('Enter підтверджує виділений варіант', () => {
    const onCommit = vi.fn();
    const node = host();
    const { input } = mountListEditor(node, {
      options: Options, selected: '1', initialQuery: '', ariaLabel: 'Fuel', onCommit,
    });

    key(input, 'ArrowDown');
    key(input, 'Enter');

    expect(onCommit).toHaveBeenCalledOnce();
    expect(onCommit).toHaveBeenCalledWith('2', false);
  });

  it('друк шукає за назвою І кодом без урахування регістру, Enter бере перший збіг', () => {
    const onCommit = vi.fn();
    const node = host();
    const { input } = mountListEditor(node, {
      options: Options, selected: null, initialQuery: '', ariaLabel: 'Fuel', onCommit,
    });

    type(input, 'FUEL');
    expect([...document.querySelectorAll('[role="option"]')].map((item) => item.firstElementChild?.textContent))
      .toEqual(['Diesel fuel', 'Fuel oil']);

    type(input, 'mzt');
    key(input, 'Enter');

    expect(onCommit).toHaveBeenCalledWith('3', false);
  });

  it('клік по варіанту підтверджує його', () => {
    const onCommit = vi.fn();
    const node = host();
    mountListEditor(node, { options: Options, selected: null, initialQuery: '', ariaLabel: 'Fuel', onCommit });

    const diesel = [...document.querySelectorAll<HTMLElement>('[role="option"]')].find(
      (item) => item.firstElementChild?.textContent === 'Diesel fuel',
    );
    diesel?.click();

    expect(onCommit).toHaveBeenCalledWith('2', false);
  });

  it('Enter без жодного збігу НЕ стирає комірку', () => {
    const onCommit = vi.fn();
    const node = host();
    const { input } = mountListEditor(node, {
      options: Options, selected: '1', initialQuery: '', ariaLabel: 'Fuel', onCommit,
    });

    type(input, 'uranium');
    key(input, 'Enter');

    expect(onCommit).not.toHaveBeenCalled();
    expect(document.body.textContent).toContain('grid.listNothingFound');
  });

  it('Tab без вибору нічого не змінює; Tab після стрілки — підтверджує й іде далі', () => {
    const onCommit = vi.fn();
    const node = host();
    const { input } = mountListEditor(node, {
      options: Options, selected: '1', initialQuery: '', ariaLabel: 'Fuel', onCommit,
    });

    key(input, 'Tab');
    expect(onCommit).not.toHaveBeenCalled();

    key(input, 'ArrowDown');
    key(input, 'Tab');
    expect(onCommit).toHaveBeenCalledWith('2', true);
  });

  it('фокус переходить у поле пошуку після кадру', () => {
    vi.useFakeTimers();
    const node = host();
    const { input } = mountListEditor(node, {
      options: Options, selected: null, initialQuery: '', ariaLabel: 'Fuel', onCommit: vi.fn(),
    });

    vi.runAllTimers();

    expect(document.activeElement).toBe(input);
    expect(input.getAttribute('role')).toBe('combobox');
    expect(input.getAttribute('aria-label')).toBe('Fuel');
  });
});

describe('mountListEditor — великий довідник і завантаження', () => {
  it('показує не більше MaxShownOptions і каже, скільки лишилось', () => {
    const many: ListOption[] = Array.from({ length: 50_000 }, (_, index) => ({
      value: String(index + 1),
      label: `Entry ${String(index + 1)}`,
    }));
    const node = host();
    mountListEditor(node, { options: many, selected: null, initialQuery: '', ariaLabel: 'Big', onCommit: vi.fn() });

    expect(document.querySelectorAll('[role="option"]')).toHaveLength(MaxShownOptions);
    expect(document.body.textContent).toContain(`count=${String(50_000 - MaxShownOptions)}`);
  });

  it('довідник, що ще їде, — «завантаження», а не порожній перелік', () => {
    const onCommit = vi.fn();
    const node = host();
    const { input } = mountListEditor(node, {
      options: null, selected: null, initialQuery: '', ariaLabel: 'Fuel', onCommit,
    });

    expect(document.body.textContent).toContain('grid.listLoading');
    expect(document.querySelector('[role="listbox"]')?.getAttribute('aria-busy')).toBe('true');

    key(input, 'Enter');
    expect(onCommit).not.toHaveBeenCalled();
  });
});

describe('filterOptions', () => {
  it('кожне слово запиту — у будь-якому порядку', () => {
    expect(filterOptions(Options, 'oil fuel').map((option) => option.value)).toEqual(['3']);
  });
});

describe('createListCellEditor — як його бачить RevoGrid', () => {
  it('редагування, почате з літери, одразу стає пошуком', () => {
    const save = vi.fn();
    const editor = createListCellEditor(() => ({ options: Options }), 'Fuel')(
      { value: '1' } as unknown as ColumnDataSchemaModel,
      save,
    );

    editor.editCell = { val: 'd' } as unknown as EditCell;
    editor.render(h);
    editor.element = host();
    editor.componentDidRender?.();

    const input = editor.element.querySelector('input') as HTMLInputElement;
    expect(input.value).toBe('d');

    key(input, 'Enter');

    // ⚠ Порожній рядок — очищення (`coerce` → `null`); тут обрано «Diesel».
    expect(save).toHaveBeenCalledWith('2', false);
  });

  it('«очистити» зберігає порожній рядок — `coerce` зробить із нього `null`', () => {
    const save = vi.fn();
    const editor = createListCellEditor(() => ({ options: Options }), 'Fuel')(
      { value: '1' } as unknown as ColumnDataSchemaModel,
      save,
    );

    editor.editCell = { val: '1' } as unknown as EditCell;
    editor.element = host();
    editor.componentDidRender?.();

    const input = editor.element.querySelector('input') as HTMLInputElement;
    key(input, 'ArrowUp');
    key(input, 'Enter');

    expect(save).toHaveBeenCalledWith('', false);
  });
});
