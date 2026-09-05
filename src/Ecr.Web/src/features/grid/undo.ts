/**
 * Стек команд для Undo/Redo (критерій FQ-1 №6).
 *
 * ⚠ **Власна модель команд**, а не «історія станів» бібліотеки. Копіювати
 * весь зріз на кожне натискання клавіші означало б тримати в пам'яті
 * п'ятдесят копій таблиці 500×60 — і саме на важкій таблиці, заради якої все
 * це й робиться. Команда зберігає лише те, що змінилося.
 */

/** Зміна однієї комірки: що було і що стало. */
export interface CellEdit {
  rowKey: string;
  columnCode: string;
  before: unknown;
  after: unknown;
}

/**
 * Крок історії.
 *
 * ⚠ Вставка діапазону — це **один** крок, а не сотні. Інакше одне Ctrl+V
 * з'їдало б усю глибину історії, і Ctrl+Z відкочував би вставку по комірці —
 * тобто робив би саме те, чого користувач не просив.
 */
export interface HistoryStep {
  /** Що саме сталося; показується користувачеві. */
  label: string;
  edits: CellEdit[];
}

/** Скільки кроків історії тримати. */
export const HistoryLimit = 50;

/**
 * Стек скасування в межах ОДНІЄЇ таблиці.
 *
 * ⛔ Історія скидається при переході на іншу таблицю. Крок, застосований до
 * чужого зрізу, писав би значення в комірки з тими самими кодами, але іншого
 * документа — і виглядало б це як звичайне редагування.
 */
export class UndoStack {
  private readonly done: HistoryStep[] = [];
  private readonly undone: HistoryStep[] = [];
  private scope: string;

  constructor(scope: string) {
    this.scope = scope;
  }

  /** Скільки кроків можна скасувати. */
  get depth(): number {
    return this.done.length;
  }

  /** Чи є що скасовувати. */
  get canUndo(): boolean {
    return this.done.length > 0;
  }

  /** Чи є що повторювати. */
  get canRedo(): boolean {
    return this.undone.length > 0;
  }

  /**
   * Переводить стек на іншу таблицю.
   *
   * Повертає <c>true</c>, якщо історію скинуто.
   */
  rescope(scope: string): boolean {
    if (scope === this.scope) return false;

    this.scope = scope;
    this.done.length = 0;
    this.undone.length = 0;

    return true;
  }

  /**
   * Записує крок.
   *
   * ⚠ Новий крок стирає гілку redo — як у будь-якому редакторі: після
   * скасування і нової правки «повторити» не має сенсу, а лишене redo
   * застосувало б зміну поверх іншої історії.
   */
  push(step: HistoryStep): void {
    if (step.edits.length === 0) return;

    this.done.push(step);
    this.undone.length = 0;

    // Найстаріший крок витісняється: п'ятдесят — це вимога, а не стеля
    // пам'яті, і тримати більше означало б платити за те, чим не
    // користуються.
    while (this.done.length > HistoryLimit) {
      this.done.shift();
    }
  }

  /** Скасовує останній крок і повертає зміни, які треба застосувати. */
  undo(): CellEdit[] | null {
    const step = this.done.pop();
    if (step === undefined) return null;

    this.undone.push(step);

    // Скасування йде у ЗВОРОТНОМУ порядку: дві правки однієї комірки в
    // одному кроці інакше відкотилися б до проміжного значення.
    return [...step.edits]
      .reverse()
      .map((edit) => ({ ...edit, before: edit.after, after: edit.before }));
  }

  /** Повторює скасований крок. */
  redo(): CellEdit[] | null {
    const step = this.undone.pop();
    if (step === undefined) return null;

    this.done.push(step);

    return step.edits;
  }
}
