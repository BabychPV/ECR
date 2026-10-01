import { useMemo, useSyncExternalStore } from 'react';

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
 * такою і після наступного збереження тієї самої комірки.
 *
 * ⛔ Після перезавантаження значок приходить зі ЗРІЗУ
 * (`TableSliceDto.outOfWindowCells`: комірки, чия остання зміна в
 * `aud.CellChange` — поза вікном). Доти він жив лише в пам'яті вкладки й зникав
 * після F5, хоча журнал змін його пам'ятав. `useOutOfWindowMarks` об'єднує обидва
 * джерела: зріз — те, що було до відкриття сторінки, відповіді `PATCH` — те, що
 * записано після.
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

/**
 * Об'єднання позначок зі зрізу й із відповідей `PATCH` цієї вкладки.
 *
 * ⚠ Повертає `recorded` тим самим об'єктом, коли зріз нічого не додає: сітка
 * мемоізує колонки за посиланням, і новий `Set` на кожен рендер перебудовував
 * би їх без причини.
 */
export function mergeOutOfWindow(
  fromSlice: readonly string[] | null | undefined,
  recorded: ReadonlySet<string>,
): ReadonlySet<string> {
  if (fromSlice == null || fromSlice.length === 0) return recorded;

  return new Set([...fromSlice, ...recorded]);
}

/**
 * Позначки зрізу для сітки; перемальовує її, щойно прийшла нова відповідь.
 *
 * @param fromSlice `TableSliceDto.outOfWindowCells` — позначки, збережені
 *   сервером; саме вони переживають перезавантаження сторінки.
 */
export function useOutOfWindowMarks(
  tableInstanceId: number,
  periodKey: number,
  fromSlice?: readonly string[] | null,
): ReadonlySet<string> {
  const snapshot = (): ReadonlySet<string> =>
    outOfWindowMarksOf(tableInstanceId, periodKey);

  const recorded = useSyncExternalStore(subscribe, snapshot, snapshot);

  return useMemo(() => mergeOutOfWindow(fromSlice, recorded), [fromSlice, recorded]);
}

/** Лише для тестів: скидає стан модуля між випадками. */
export function resetOutOfWindowMarks(): void {
  marks.clear();
}
