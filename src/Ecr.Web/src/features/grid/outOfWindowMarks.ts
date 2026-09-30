import { useSyncExternalStore } from 'react';

/**
 * Комірки, записані за політикою `Warn` поза вікном доступу (`ФВ-2.16`, `D-239`).
 *
 * ⚠ Джерело — рівно `PatchCellsResponse.outOfWindow` (адреси `rowKey:columnCode`,
 * той самий формат, що й `cellKey` у `permissions.ts`); сервер пише ту саму
 * позначку в `aud.CellChange.IsOutOfWindow`, тож значок на сітці і рядок журналу
 * змін говорять про одне.
 *
 * ⛔ Модуль НЕ імпортується статично з графа `DocumentPage` (`D-132`, бюджет
 * маршруту): `useCellPatch.ts` тягне його через `import()` лише тоді, коли
 * відповідь справді назвала такі комірки, а статично його бере тільки
 * `DocumentGrid` — вона й так живе в лінивому чанку сітки.
 *
 * ⚠ Позначки лише додаються, доки жива вкладка: правка поза вікном лишається
 * такою і після наступного збереження тієї самої комірки. Після перезаходу
 * позначка видна в журналі змін (`AuditPage`, фільтр однієї комірки), а не на
 * сітці — зріз цієї ознаки не несе.
 */

const marks = new Map<string, ReadonlySet<string>>();
const listeners = new Set<() => void>();
const Empty: ReadonlySet<string> = new Set();

function sliceKey(tableInstanceId: number, periodKey: number): string {
  return `${tableInstanceId}:${periodKey}`;
}

/** Додає адреси з відповіді `PATCH` до позначок зрізу й будить підписані сітки. */
export function recordOutOfWindow(
  tableInstanceId: number,
  periodKey: number,
  cells: readonly string[],
): void {
  if (cells.length === 0) return;

  const key = sliceKey(tableInstanceId, periodKey);

  // ⚠ Новий `Set`, а не мутація наявного: `useSyncExternalStore` порівнює
  // знімки за посиланням, і мутований набір не перемалював би сітку.
  marks.set(key, new Set([...(marks.get(key) ?? Empty), ...cells]));
  for (const listener of listeners) listener();
}

/** Позначки зрізу; той самий об'єкт, доки вони не змінилися. */
export function outOfWindowMarksOf(
  tableInstanceId: number,
  periodKey: number,
): ReadonlySet<string> {
  return marks.get(sliceKey(tableInstanceId, periodKey)) ?? Empty;
}

function subscribe(listener: () => void): () => void {
  listeners.add(listener);

  return () => {
    listeners.delete(listener);
  };
}

/** Позначки зрізу для сітки; перемальовує її, щойно прийшла нова відповідь. */
export function useOutOfWindowMarks(
  tableInstanceId: number,
  periodKey: number,
): ReadonlySet<string> {
  const snapshot = (): ReadonlySet<string> =>
    outOfWindowMarksOf(tableInstanceId, periodKey);

  return useSyncExternalStore(subscribe, snapshot, snapshot);
}

/** Лише для тестів: скидає стан модуля між випадками. */
export function resetOutOfWindowMarks(): void {
  marks.clear();
}
