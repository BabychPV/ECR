/**
 * ✎ D-461: причини «ручного значення замість формули», які людина ввела в діалозі.
 *
 * ⛔ Той самий прийом, що й `confirmedEdits.ts`: запит збирають кілька шляхів (сітка, документ за розмонтовану
 * сітку, маячок при закритті вкладки) зі СХОВИЩА правок, у якому причини немає. Тому причина лежить тут, а поле
 * `overrideReason` ставить сам `buildRequest` — жоден шлях не може її забути.
 *
 * ⚠ Причина прив'язана до комірки, а не до значення: повтор правки тієї самої комірки (відхилена й виправлена,
 * undo/redo) — те саме рішення людини. Живе до закриття документа (`resetOverrideReasons` у `autosave.ts`).
 */

const reasons = new Map<string, Map<string, string>>();

// ⚠ Попередня причина комірки (до останньої позначки): крок історії, зроблений поверх ще НЕ збереженого ручного
// значення, мусить знати, з якою причиною це значення стояло, — щоб Undo повернуло його саме таким (D-461).
const previous = new Map<string, Map<string, string | undefined>>();

interface CellRef {
  rowKey: string;
  columnCode: string;
}

function sliceKeyOf(tableInstanceId: number, periodKey: number): string {
  return `${tableInstanceId}:${periodKey}`;
}

function cellKeyOf(cell: CellRef): string {
  return `${cell.rowKey}:${cell.columnCode}`;
}

/** Людина назвала причину ручного значення для цих комірок. */
export function markOverrideReason(
  tableInstanceId: number,
  periodKey: number,
  cells: Iterable<CellRef>,
  reason: string,
): void {
  const key = sliceKeyOf(tableInstanceId, periodKey);
  const map = reasons.get(key) ?? new Map<string, string>();
  const before = previous.get(key) ?? new Map<string, string | undefined>();

  for (const cell of cells) {
    const cellKey = cellKeyOf(cell);
    before.set(cellKey, map.get(cellKey));
    map.set(cellKey, reason);
  }

  if (map.size > 0) {
    reasons.set(key, map);
    previous.set(key, before);
  }
}

/** Причина комірки ДО останньої `markOverrideReason` (або `undefined`). */
export function previousOverrideReasonOf(tableInstanceId: number, periodKey: number, cell: CellRef): string | undefined {
  return previous.get(sliceKeyOf(tableInstanceId, periodKey))?.get(cellKeyOf(cell));
}

/** Причина комірки або `undefined`. */
export function overrideReasonOf(tableInstanceId: number, periodKey: number, cell: CellRef): string | undefined {
  return reasons.get(sliceKeyOf(tableInstanceId, periodKey))?.get(cellKeyOf(cell));
}

/** Документ закрито — причини більше нічого не значать. */
export function resetOverrideReasons(): void {
  reasons.clear();
  previous.clear();
}
