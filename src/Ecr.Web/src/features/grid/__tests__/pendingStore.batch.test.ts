import { afterEach, describe, expect, it, vi } from 'vitest';
import type { PendingEdit } from '../useCellPatch';
import {
  markPendingRejected,
  openDocument,
  pendingRejections,
  pendingSlice,
  putPendingEdit,
  putPendingEdits,
  resetPending,
  sendableEdits,
  subscribePending,
} from '../pendingStore';

/**
 * Пакетний запис правок (`putPendingEdits`) — вставка з Excel.
 *
 * ⛔ Що ламалося. `saveThroughStore` кликав `putPendingEdit` у циклі, а кожен
 * виклик копіював УВЕСЬ зріз і сповіщав підписників: вставка N комірок — N
 * копій зростаючої мапи (квадратично) і N сповіщень у синхронному `onPaste`.
 *
 * ⚠ Доказ — лічильники (копії мапи, сповіщення), а не секундомір: час у CI
 * плаває, а кількість копій — ні.
 */

const Table = 1;
const Period = 202609;

function edit(rowKey: string, columnCode: string, value: unknown): PendingEdit {
  return { rowKey, columnCode, value, isEmpty: false, baseVersion: 'v1' };
}

/** Прямокутник правок `rows × columns` — форма вставки з Excel. */
function block(rows: number, columns: number): PendingEdit[] {
  const edits: PendingEdit[] = [];
  for (let r = 0; r < rows; r += 1) {
    for (let c = 0; c < columns; c += 1) edits.push(edit(`r${String(r)}`, `C${String(c)}`, r * columns + c));
  }

  return edits;
}

/**
 * Рахує `new Map(<інша Map>)` — тобто КОПІЇ мапи, — поки виконується `run`.
 *
 * ⚠ Глобаль підміняється лише на час виклику: React і сам Vitest поза цим
 * вікном бачать справжній `Map`.
 */
function countMapCopies(run: () => void): number {
  const Original = Map;
  let copies = 0;

  class CountingMap<K, V> extends Original<K, V> {
    constructor(entries?: Iterable<readonly [K, V]> | null) {
      super(entries);
      if (entries instanceof Original) copies += 1;
    }
  }

  vi.stubGlobal('Map', CountingMap);
  try {
    run();
  } finally {
    vi.unstubAllGlobals();
  }

  return copies;
}

afterEach(() => {
  vi.unstubAllGlobals();
  resetPending();
});

describe('putPendingEdits — вставка без квадратичної вартості', () => {
  it('30 000 правок: одне сповіщення і одна копія мапи зрізу', () => {
    openDocument(1);
    const edits = block(1000, 30);
    let notified = 0;
    const unsubscribe = subscribePending(() => {
      notified += 1;
    });

    const copies = countMapCopies(() => putPendingEdits(Table, Period, edits));
    unsubscribe();

    expect(notified).toBe(1);
    expect(copies).toBe(1);
    expect(pendingSlice(Table, Period).size).toBe(30_000);
  });

  it('контроль лічильника: поштучний запис тих самих правок дає N сповіщень і N копій', () => {
    // ⚠ Без цього контролю тест вище міг би бути зеленим через зламаний
    // лічильник, а не через пакет.
    openDocument(1);
    const edits = block(10, 3);
    let notified = 0;
    const unsubscribe = subscribePending(() => {
      notified += 1;
    });

    const copies = countMapCopies(() => {
      for (const e of edits) putPendingEdit(Table, Period, e);
    });
    unsubscribe();

    expect(notified).toBe(30);
    expect(copies).toBe(30);
  });

  it('порожній пакет нічого не змінює і нікого не будить', () => {
    openDocument(1);
    let notified = 0;
    const unsubscribe = subscribePending(() => {
      notified += 1;
    });

    putPendingEdits(Table, Period, []);
    unsubscribe();

    expect(notified).toBe(0);
    expect(pendingSlice(Table, Period).size).toBe(0);
  });
});

describe('putPendingEdits ≡ послідовність putPendingEdit', () => {
  /**
   * Стартовий стан із відмовами обох рівнів:
   *   r1:C1 — відмова комірки; r1:C2 — без відмови;
   *   r2:C1 — відмова рядка;   r3:C1 — відмова комірки (пакет її не чіпає);
   *   r4:C1 — відмова рядка    (пакет її не чіпає).
   */
  function seed(): void {
    openDocument(1);
    putPendingEdits(Table, Period, [
      edit('r1', 'C1', 'abc'),
      edit('r1', 'C2', 7),
      edit('r2', 'C1', 5),
      edit('r3', 'C1', 'xyz'),
      edit('r4', 'C1', 9),
    ]);
    markPendingRejected(Table, Period, [
      { edit: edit('r1', 'C1', 'abc'), message: 'cell', scope: 'cell' },
      { edit: edit('r2', 'C1', 5), message: 'row', scope: 'row' },
      { edit: edit('r3', 'C1', 'xyz'), message: 'cell', scope: 'cell' },
      { edit: edit('r4', 'C1', 9), message: 'row', scope: 'row' },
    ]);
  }

  /** Знімок стану сховища, придатний для `toEqual`. */
  function snapshot(): unknown {
    return {
      slice: [...pendingSlice(Table, Period)],
      rejections: [...pendingRejections(Table, Period)],
      sendable: sendableEdits(Table, Period).map((e) => `${e.rowKey}:${e.columnCode}`).sort(),
    };
  }

  const batch: PendingEdit[] = [
    // Та сама комірка, ТЕ САМЕ значення — відпускає відмову комірки (`V-01`).
    edit('r1', 'C1', 'abc'),
    // Інша комірка рядка з відмовою рівня рядка — відпускає її.
    edit('r2', 'C7', 1),
    // Інша комірка рядка з відмовою рівня КОМІРКИ — її не відпускає.
    edit('r3', 'C2', 2),
    // Нова комірка нового рядка.
    edit('r5', 'C1', 3),
    // Дублікат у пакеті: перемагає остання правка, як і поштучно.
    edit('r5', 'C1', 4),
  ];

  it('той самий стан: правки, відмови комірки й рядка, sendable', () => {
    seed();
    for (const e of batch) putPendingEdit(Table, Period, e);
    const sequential = snapshot();
    resetPending();

    seed();
    putPendingEdits(Table, Period, batch);
    const batched = snapshot();

    expect(batched).toEqual(sequential);

    // І сам стан — той, що вимагає `V-01`, а не лише «однаково зламаний».
    const held = [...pendingRejections(Table, Period).keys()].sort();
    expect(held).toEqual(['r3:C1', 'r4:C1']);
    expect(pendingSlice(Table, Period).get('r5:C1')?.value).toBe(4);
  });
});
