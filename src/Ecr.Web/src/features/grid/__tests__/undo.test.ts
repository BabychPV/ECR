import { describe, it, expect } from 'vitest';
import { HistoryLimit, UndoStack, type CellEdit } from '@/features/grid/undo';

/**
 * Undo/Redo ≥50 кроків — критерій FQ-1 №6. Потребує **власної моделі
 * команд**: не всі grid-бібліотеки дозволяють її вбудувати без форку, і саме
 * це перевіряється прототипом на Етапі 0.
 */
function edit(value: number): CellEdit {
  return { rowKey: 'R1', columnCode: 'C1', before: value - 1, after: value };
}

describe('Undo/Redo', () => {
  it('скасовує останню зміну', () => {
    const stack = new UndoStack('t1');
    stack.push({ label: 'Правка', edits: [edit(10)] });

    const undone = stack.undo();

    expect(undone).toEqual([{ rowKey: 'R1', columnCode: 'C1', before: 10, after: 9 }]);
    expect(stack.canUndo).toBe(false);
    expect(stack.canRedo).toBe(true);
  });

  it('тримає щонайменше 50 кроків історії', () => {
    const stack = new UndoStack('t1');

    for (let i = 0; i < HistoryLimit + 10; i++) {
      stack.push({ label: `Крок ${i}`, edits: [edit(i)] });
    }

    expect(HistoryLimit).toBeGreaterThanOrEqual(50);
    expect(stack.depth).toBe(HistoryLimit);

    // Витісняється НАЙСТАРІШИЙ крок: останні п'ятдесят лишаються доступними.
    for (let i = 0; i < HistoryLimit; i++) {
      expect(stack.undo()).not.toBeNull();
    }

    expect(stack.undo()).toBeNull();
  });

  it('повторює скасовану зміну', () => {
    const stack = new UndoStack('t1');
    stack.push({ label: 'Правка', edits: [edit(10)] });
    stack.undo();

    expect(stack.redo()).toEqual([{ rowKey: 'R1', columnCode: 'C1', before: 9, after: 10 }]);
    expect(stack.canUndo).toBe(true);
  });

  it('скидає історію при переході на іншу таблицю', () => {
    // ⛔ Крок, застосований до чужого зрізу, писав би значення в комірки з
    // тими самими кодами, але іншого документа — і виглядало б це як
    // звичайне редагування.
    const stack = new UndoStack('t1');
    stack.push({ label: 'Правка', edits: [edit(10)] });

    expect(stack.rescope('t2')).toBe(true);
    expect(stack.canUndo).toBe(false);
    expect(stack.canRedo).toBe(false);

    // Повторний перехід на ту саму таблицю історію не чіпає.
    expect(stack.rescope('t2')).toBe(false);
  });

  it('вставка діапазону — це ОДИН крок історії, а не сотні', () => {
    const stack = new UndoStack('t1');

    const paste = Array.from({ length: 300 }, (_, i) => ({
      rowKey: `R${i}`,
      columnCode: 'C1',
      before: null,
      after: i,
    }));

    stack.push({ label: 'Вставка 300 комірок', edits: paste });

    expect(stack.depth).toBe(1);
    expect(stack.undo()).toHaveLength(300);
    expect(stack.canUndo).toBe(false);
  });

  it('нова правка після скасування стирає гілку повтору', () => {
    const stack = new UndoStack('t1');
    stack.push({ label: 'Перша', edits: [edit(1)] });
    stack.undo();
    stack.push({ label: 'Друга', edits: [edit(2)] });

    expect(stack.canRedo).toBe(false);
  });

  it('порожній крок в історію не потрапляє', () => {
    // Натиснута і скасована клавіша не має з'їдати глибину історії.
    const stack = new UndoStack('t1');
    stack.push({ label: 'Нічого не змінилося', edits: [] });

    expect(stack.depth).toBe(0);
  });
});
