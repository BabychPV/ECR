import type { PendingEdit } from './useCellPatch';

/**
 * Правки, чий `PATCH` зараз У ДОРОЗІ, — на рівні ДОКУМЕНТА (`AN-104`: `D1-01`, `D1-03`).
 *
 * ⛔ Доти реєстр «у дорозі» був лише в сітці (`useRef` у `DocumentGrid`), і бачила
 * його тільки вона. Звідси два дефекти:
 * - `D1-01`: другий запит у той самий рядок, поки перший летить, брав версію з кешу,
 *   яку підніме лише відповідь першого, — і діставав `409` на власних правках
 *   (а безхазяйний зріз, `saveOrphanSlice`, не мав навіть дедуплікації);
 * - `D1-03`: маячок закриття вкладки віз комірки, що вже летять, зі старою версією —
 *   `409` відкидав увесь пакет разом із новішими правками, мовчки.
 *
 * ⚠ Модульний, а не React-стан — з тієї ж причини, що й `pendingStore.ts`: запит
 * переживає розмонтування сітки, яка його відправила.
 */

/** Ключ комірки — той самий, що `cellKey` у `pendingStore.ts` (`rowKey:columnCode`). */
type CellKey = string;

function cellKey(edit: Pick<PendingEdit, 'rowKey' | 'columnCode'>): CellKey {
  return `${edit.rowKey}:${edit.columnCode}`;
}

const bySlice = new Map<string, Map<CellKey, PendingEdit>>();

/** Хто чекає, доки звільняться рядки (відкладені правки, `deferUntilInFlightSettles`). */
const waiting = new Set<() => void>();

function keyOfSlice(tableInstanceId: number, periodKey: number): string {
  return `${String(tableInstanceId)}:${String(periodKey)}`;
}

/**
 * Позначає правки як надіслані.
 *
 * @returns Зняття позначки — ЛИШЕ своїх записів: та сама комірка могла тим часом
 * полетіти новішим запитом, і його запис чужий цьому виклику.
 */
export function beginInFlight(
  tableInstanceId: number,
  periodKey: number,
  edits: readonly PendingEdit[],
): () => void {
  const key = keyOfSlice(tableInstanceId, periodKey);
  const cells = bySlice.get(key) ?? new Map<CellKey, PendingEdit>();
  bySlice.set(key, cells);

  for (const edit of edits) cells.set(cellKey(edit), edit);

  return () => {
    for (const edit of edits) {
      const k = cellKey(edit);
      if (cells.get(k) === edit) cells.delete(k);
    }

    if (cells.size === 0 && bySlice.get(key) === cells) bySlice.delete(key);

    // ⚠ Будь-яке звільнення будить тих, хто чекав: повтор сам перевірить, чи його
    // рядки вже вільні, і за потреби відкладеться знову.
    const callbacks = [...waiting];
    waiting.clear();
    for (const callback of callbacks) callback();
  };
}

/**
 * Правка комірки, чий `PATCH` зараз у дорозі; `undefined` — нічого не летить.
 *
 * ⛔ `G1-02`: «повернення до збереженого», поки летить інше значення тієї самої
 * комірки, — не скасування, а НОВА правка: сервер от-от прийме те, що летить.
 */
export function inFlightEdit(
  tableInstanceId: number,
  periodKey: number,
  cell: Pick<PendingEdit, 'rowKey' | 'columnCode'>,
): PendingEdit | undefined {
  return bySlice.get(keyOfSlice(tableInstanceId, periodKey))?.get(cellKey(cell));
}

/** Рядки зрізу, для яких зараз летить запит. */
export function inFlightRowKeys(tableInstanceId: number, periodKey: number): ReadonlySet<string> {
  const cells = bySlice.get(keyOfSlice(tableInstanceId, periodKey));

  return new Set(cells === undefined ? [] : [...cells.values()].map((edit) => edit.rowKey));
}

/** Чи летить зараз бодай один запит документа. */
export function hasInFlight(): boolean {
  for (const cells of bySlice.values()) if (cells.size > 0) return true;

  return false;
}

/** Повторити відкладене, щойно звільниться будь-який запит у дорозі. */
export function deferUntilInFlightSettles(retry: () => void): void {
  waiting.add(retry);
}

/**
 * Ділить правки на ті, що можна слати зараз, і ті, чий рядок уже летить.
 *
 * ⛔ `D1-01`: рядок, чий `PATCH` у дорозі, вдруге не шлеться — версія в кеші ще
 * стара, її оновить лише відповідь першого запиту. Правки решти рядків ідуть як і
 * раніше (§10.2: два одночасні запити РІЗНИХ рядків — навмисна поведінка).
 *
 * @param exempt Рядки, версію яких викликач назвав сам («Keep mine»): для них кеш
 * не джерело версії, і відкладати їх нема причини.
 */
export function splitByInFlight(
  tableInstanceId: number,
  periodKey: number,
  edits: readonly PendingEdit[],
  exempt?: ReadonlyMap<string, string>,
): { now: PendingEdit[]; deferred: PendingEdit[] } {
  const busy = inFlightRowKeys(tableInstanceId, periodKey);
  const now: PendingEdit[] = [];
  const deferred: PendingEdit[] = [];

  for (const edit of edits) {
    if (busy.has(edit.rowKey) && exempt?.has(edit.rowKey) !== true) deferred.push(edit);
    else now.push(edit);
  }

  return { now, deferred };
}

/**
 * Скидання разом зі сховищем правок (`resetPending`: вихід із документа, тести).
 *
 * ⚠ Запити, що ще летять, після цього знімають свої позначки вхолосту: їхня мапа
 * вже відʼєднана від реєстру.
 */
export function resetInFlight(): void {
  bySlice.clear();
  waiting.clear();
}
