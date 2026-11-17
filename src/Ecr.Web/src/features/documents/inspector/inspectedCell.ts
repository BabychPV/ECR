import { useSyncExternalStore } from 'react';

/**
 * Комірка, яку показують вкладки History та Info інспектора документа (`UI-25`).
 *
 * ⛔ Сховище модульне, а не проп: комірку під курсором знає рядок формули
 * сітки (`GridFormulaBar` у лінивому чанку `SheetTables`), а інспектор —
 * інший лінивий чанк. Прямого зв'язку між ними немає (той самий прийом, що й
 * `focusStore.ts`/`cellNavigation.ts`). Модуль крихітний і НЕ тягне ні
 * RevoGrid, ні React-дерева сітки, тож бюджет `D-132` не зачіпає.
 *
 * ⚠ ОДНА комірка на застосунок — остання, на яку поставили курсор. На аркуші
 * змонтовано кілька сіток, і інспектор показує ту, з якою людина працювала
 * щойно, а не першу на сторінці.
 */
export interface InspectedCell {
  readonly tableInstanceId: number;
  /** Період ПЕРЕГЛЯДУ (зрізу сітки) — адреса сторінки; комірка іншого періоду інспектору не показується. */
  readonly periodKey: number;
  /**
   * ✎ LAND-YEAR L7b (п. 3 плану): період-ВЛАСНИК комірки — місячна колонка m таблиці «місяці в колонках» належить
   * періоду m (``monthColumns`` відповіді ``submission-target``: ``monthNumber`` + ``periodKey``); ``null`` — колонка не
   * місячна (власник — період перегляду / якір року) або місяць ще невідомий.
   */
  readonly ownerPeriodKey?: number | null;
  readonly rowKey: string;
  readonly rowLabel: string;
  readonly columnCode: string;
  /** `ColumnDto.id` — адреса для `GET /audit/cells` (`columnDefId`). */
  readonly columnDefId: number;
  readonly columnHeader: string;
  readonly unitSymbol: string | null;
  readonly dataType: string;
  readonly scale: number | null;
  readonly isCalculated: boolean;
  readonly isReadOnly: boolean;
  /** Значення в канонічному записі; порожній рядок — не заповнено. */
  readonly value: string;
}

let current: InspectedCell | null = null;
const listeners = new Set<() => void>();

function same(a: InspectedCell | null, b: InspectedCell | null): boolean {
  if (a === null || b === null) return a === b;

  return (Object.keys(a) as (keyof InspectedCell)[]).every((key) => a[key] === b[key]);
}

/**
 * Записує комірку під курсором.
 *
 * ⚠ Та сама комірка з тим самим значенням нікого не будить: рядок формули
 * перемальовується частіше, ніж змінюється комірка.
 */
export function publishInspectedCell(cell: InspectedCell | null): void {
  if (same(current, cell)) return;

  current = cell;
  for (const listener of listeners) listener();
}

/** Остання комірка під курсором; `null` — курсор у сітку ще не ставили. */
export function inspectedCell(): InspectedCell | null {
  return current;
}

function subscribe(listener: () => void): () => void {
  listeners.add(listener);

  return () => {
    listeners.delete(listener);
  };
}

/** Хук для інспектора. */
export function useInspectedCell(): InspectedCell | null {
  return useSyncExternalStore(subscribe, inspectedCell, inspectedCell);
}

/** Скидає комірку — сторінка документа розмонтовується або змінюється документ. */
export function clearInspectedCell(): void {
  publishInspectedCell(null);
}
