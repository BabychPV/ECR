import { useSyncExternalStore } from 'react';
import type { GridSelection } from './selection';

/**
 * Виділений діапазон сітки — окреме сховище поза станом `DocumentGrid`
 * (UI-23, рядок стану сітки), за тим самим прийомом і з тієї самої причини,
 * що `focusStore.ts`: стан сітки на кожен рух виділення перебудовував би опис
 * колонок; сховище будить рівно `GridStatusBar`.
 *
 * ⚠ Ключ — зріз (`tableInstanceId:periodKey`): на аркуші змонтовано кілька
 * сіток, і в кожної своє виділення.
 */

type SliceKey = string;

function sliceKey(tableInstanceId: number, periodKey: number): SliceKey {
  return `${String(tableInstanceId)}:${String(periodKey)}`;
}

const selected = new Map<SliceKey, GridSelection['range']>();
const listeners = new Set<() => void>();

/** Підписка на будь-яку зміну виділення; повертає відписку. */
export function subscribeSelection(listener: () => void): () => void {
  listeners.add(listener);

  return () => {
    listeners.delete(listener);
  };
}

/** Виділений прямокутник зрізу; `null` — виділення ще не було. Посилання стабільне. */
export function selectedRange(tableInstanceId: number, periodKey: number): GridSelection['range'] | null {
  return selected.get(sliceKey(tableInstanceId, periodKey)) ?? null;
}

/** Записує виділення зрізу; той самий прямокутник нікого не будить. */
export function publishSelection(
  tableInstanceId: number,
  periodKey: number,
  range: GridSelection['range'] | null,
): void {
  const key = sliceKey(tableInstanceId, periodKey);
  const current = selected.get(key) ?? null;

  if (current === null && range === null) return;

  if (
    current !== null &&
    range !== null &&
    current.fromRow === range.fromRow &&
    current.toRow === range.toRow &&
    current.fromColumn === range.fromColumn &&
    current.toColumn === range.toColumn
  ) {
    return;
  }

  if (range === null) selected.delete(key);
  else selected.set(key, range);

  for (const listener of listeners) listener();
}

/** Виділення зрізу для компонента. */
export function useSelectedRange(tableInstanceId: number, periodKey: number): GridSelection['range'] | null {
  return useSyncExternalStore(
    subscribeSelection,
    () => selectedRange(tableInstanceId, periodKey),
    () => selectedRange(tableInstanceId, periodKey),
  );
}
