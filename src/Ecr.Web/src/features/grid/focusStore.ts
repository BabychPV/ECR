import { useSyncExternalStore } from 'react';
import type { FocusedCell } from './selection';

/**
 * Комірка фокуса — окреме сховище поза станом `DocumentGrid` (`UI-08`).
 *
 * ⛔ Чому не `useState` у сітці. Рядок формули має оновлюватися на КОЖЕН рух
 * курсора — а `DocumentGrid` на кожному своєму рендері перебудовує опис
 * колонок (`gridColumns`, до 60 колонок із замиканнями на кожну) і модель
 * рядків. Тримати фокус у стані сітки означало б платити цю ціну на кожну
 * стрілку клавіатури на таблиці 500×60. Тому фокус їде повз React-дерево
 * сітки: публікує його слухач DOM (`trackFocusedCell`), а читає рівно той
 * компонент, якому він потрібен, — `GridFormulaBar`.
 *
 * ⛔ Ключ — ЗРІЗ (`tableInstanceId:periodKey`), а не модуль цілком. На аркуші
 * одночасно змонтовано кілька сіток (`SheetTables.tsx` монтує їх за
 * прокруткою); єдиний глобальний фокус показував би у ВСІХ рядках формули
 * комірку тієї сітки, у якій востаннє клацнули. Той самий ключ і з тієї самої
 * причини, що в `pendingStore.ts`.
 *
 * ⚠ Сховище модульне, а не контекст: підписка має переживати перемальовування
 * сітки, а контекст помирає разом із провайдером.
 */

/** Ключ зрізу в межах документа. */
type SliceKey = string;

function sliceKey(tableInstanceId: number, periodKey: number): SliceKey {
  return `${String(tableInstanceId)}:${String(periodKey)}`;
}

const focused = new Map<SliceKey, FocusedCell>();
const listeners = new Set<() => void>();

function notify(): void {
  for (const listener of listeners) listener();
}

/** Підписка на будь-яку зміну фокуса; повертає відписку. */
export function subscribeFocus(listener: () => void): () => void {
  listeners.add(listener);

  return () => {
    listeners.delete(listener);
  };
}

/**
 * Комірка фокуса цього зрізу; `null` — курсор у сітку ще не ставили.
 *
 * ⚠ Посилання СТАБІЛЬНЕ, доки фокус не змінився: `useSyncExternalStore`
 * порівнює знімки за посиланням і на новому об'єкті щоразу кидає
 * «getSnapshot should be cached» разом із нескінченним перемальовуванням.
 */
export function focusedCell(tableInstanceId: number, periodKey: number): FocusedCell | null {
  return focused.get(sliceKey(tableInstanceId, periodKey)) ?? null;
}

/**
 * Записує комірку фокуса зрізу.
 *
 * ⚠ Повторна публікація ТІЄЇ САМОЇ комірки нікого не будить: RevoGrid шле
 * `focuscell` і тоді, коли координати не змінилися (повторний клік у ту саму
 * клітинку, вихід із редактора), а кожне таке сповіщення перемальовувало б
 * рядок формули без жодної зміни на екрані.
 */
export function publishFocus(
  tableInstanceId: number,
  periodKey: number,
  cell: FocusedCell | null,
): void {
  const key = sliceKey(tableInstanceId, periodKey);
  const current = focused.get(key) ?? null;

  if (current === null && cell === null) return;

  if (
    current !== null &&
    cell !== null &&
    current.rowIndex === cell.rowIndex &&
    current.columnIndex === cell.columnIndex
  ) {
    return;
  }

  if (cell === null) focused.delete(key);
  else focused.set(key, cell);

  notify();
}

/**
 * Скидає весь модуль.
 *
 * ⚠ Для тестів: сховище модульне й переживає кінець тесту так само, як
 * `pendingStore.resetPending` — фокус одного сценарію інакше потрапив би в
 * наступний.
 */
export function resetFocus(): void {
  focused.clear();
  notify();
}

/** Комірка фокуса цього зрізу, з підпискою на зміни. */
export function useFocusedCell(tableInstanceId: number, periodKey: number): FocusedCell | null {
  return useSyncExternalStore(
    subscribeFocus,
    () => focusedCell(tableInstanceId, periodKey),
    () => focusedCell(tableInstanceId, periodKey),
  );
}
