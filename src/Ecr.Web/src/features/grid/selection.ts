/**
 * Поточне виділення RevoGrid — у термінах індексів зрізу (аудит §10.1).
 *
 * ⛔ Модуль існує тому, що до аудиту виділення НЕ ЧИТАВ ніхто: `onPaste`
 * передавав у `planPaste` жорстко закодований якір `{ rowIndex: 0, columnIndex:
 * 0 }`, а `onCopy` серіалізував `data.rows.map(...)` — усю таблицю. Оператор
 * клацав рядок 50, колонку «C3», вставляв блок з Excel — і значення лягали з
 * рядка 1, колонки 1, мовчки перезаписуючи чужі, уже коректні дані; Ctrl+C
 * після виділення двох комірок віддавав у буфер тисячі.
 *
 * ⛔ Слухачі DOM, а не пропси React, і це не стиль. `focuscell` і `setrange`
 * оголошені на `revogr-overlay-selection` — компоненті ВСЕРЕДИНІ тіньового
 * дерева `revo-grid`, — тож у типах кореневого елемента (`JSX.RevoGrid`,
 * звідки React-обгортка бере свої пропси) відповідних `onFocuscell`/`onSetrange`
 * НЕМАЄ взагалі. Обидві події оголошені `bubbles: true, composed: true`
 * (перевірено в `@revolist/revogrid/dist/collection/.../revogr-overlay-selection.js`),
 * тобто виходять із тіньового дерева і доходять до нашого контейнера — тим
 * самим шляхом, яким уже працює нормалізація Enter (`keyboardCompat.ts`).
 *
 * ⛔ І синхронно. `getFocused()`/`getSelectedRange()` кореневого елемента
 * віддають ОБІЦЯНКИ, а `onCopy` не може чекати: після завершення обробника
 * `event.clipboardData` більше не приймає запис. Тому виділення відслідковується
 * наперед, а не питається в момент копіювання.
 *
 * ⚠ Чистий модуль поруч із `clipboard.ts`/`undo.ts` і з тієї самої причини:
 * розбір події можна перевірити без DOM веб-компонента, а логіку, схована в
 * колбеку RevoGrid, перевірялася б лише через нього — тобто ніколи.
 */

/** Прямокутник виділення в індексах зрізу; межі включні. */
export interface SelectionRange {
  readonly fromRow: number;
  readonly toRow: number;
  readonly fromColumn: number;
  readonly toColumn: number;
}

/** Що саме зараз обрано в сітці. */
export interface GridSelection {
  /**
   * Якірна комірка — та, від якої розкладається вставка.
   *
   * ⛔ Це ВЕРХНІЙ ЛІВИЙ кут виділення, а не комірка фокуса, і різниця
   * видима: виділяючи мишею (чи Shift+↑) знизу вгору, оператор лишає фокус у
   * комірці, з якої ПОЧАВ, тобто в нижньому кінці діапазону. Вставити від неї
   * означало б піти вниз від видимого виділення — не туди, куди вказує
   * позначене на екрані. Excel вставляє від кута; тут так само.
   */
  readonly anchor: { readonly rowIndex: number; readonly columnIndex: number };

  /** Виділений прямокутник; для однієї комірки — вироджений у неї. */
  readonly range: SelectionRange;
}

/** Якір за замовчуванням: кут таблиці. */
export const TableCornerAnchor = { rowIndex: 0, columnIndex: 0 } as const;

/** Ціле невід'ємне число або `null`. */
function indexOf(value: unknown): number | null {
  return typeof value === 'number' && Number.isInteger(value) && value >= 0 ? value : null;
}

/**
 * Чи стосується подія ЗВИЧАЙНОГО тіла таблиці.
 *
 * ⛔ Індекси в подіях закріплених рядків/колонок відлічуються від СВОЄЇ
 * секції, не від зрізу. Ця сітка закріплень не використовує, але прийняти
 * таку подію як звичайну означало б записати вставку в рядок з тим самим
 * номером у зовсім іншій секції — саме той клас дефекту, який §10.1 і
 * описує, лише глибше.
 */
function isMainViewport(detail: { rowType?: unknown; colType?: unknown }): boolean {
  const { rowType, colType } = detail;

  return (
    (rowType === undefined || rowType === 'rgRow') && (colType === undefined || colType === 'rgCol')
  );
}

/** Нормалізує пару кутів у прямокутник із включними межами. */
function rangeOf(x: number, y: number, x1: number, y1: number): SelectionRange {
  return {
    fromRow: Math.min(y, y1),
    toRow: Math.max(y, y1),
    fromColumn: Math.min(x, x1),
    toColumn: Math.max(x, x1),
  };
}

/**
 * Розбирає `detail` події `focuscell` (`ApplyFocusEvent & FocusRenderEvent`).
 *
 * @returns `null`, якщо подія не про тіло таблиці або не несе координат.
 */
export function selectionOfFocusEvent(detail: unknown): GridSelection | null {
  if (typeof detail !== 'object' || detail === null) return null;

  const event = detail as {
    rowType?: unknown;
    colType?: unknown;
    focus?: { x?: unknown; y?: unknown };
    end?: { x?: unknown; y?: unknown };
  };

  if (!isMainViewport(event)) return null;

  const x = indexOf(event.focus?.x);
  const y = indexOf(event.focus?.y);
  if (x === null || y === null) return null;

  // ⚠ `end` — протилежний кут виділення; його може не бути (звичайний клік),
  // і тоді діапазон вироджується у саму комірку.
  const x1 = indexOf(event.end?.x) ?? x;
  const y1 = indexOf(event.end?.y) ?? y;
  const range = rangeOf(x, y, x1, y1);

  return { anchor: { rowIndex: range.fromRow, columnIndex: range.fromColumn }, range };
}

/**
 * Розбирає `detail` події `setrange` (`RangeArea & { type }`).
 *
 * @returns `null`, якщо подія не несе повного прямокутника.
 */
export function selectionOfRangeEvent(detail: unknown): GridSelection | null {
  if (typeof detail !== 'object' || detail === null) return null;

  const event = detail as { x?: unknown; y?: unknown; x1?: unknown; y1?: unknown };

  const x = indexOf(event.x);
  const y = indexOf(event.y);
  const x1 = indexOf(event.x1);
  const y1 = indexOf(event.y1);
  if (x === null || y === null || x1 === null || y1 === null) return null;

  const range = rangeOf(x, y, x1, y1);

  // ⚠ Якір — кут прямокутника: далі вставка розкладається саме від нього, і
  // «вставити у виділене» має починатися там, де виділене починається.
  return { anchor: { rowIndex: range.fromRow, columnIndex: range.fromColumn }, range };
}

/**
 * Підписується на події виділення сітки в межах контейнера.
 *
 * @param node Контейнер, у якому живе `<RevoGrid>`.
 * @param report Викликається на кожну зміну виділення.
 * @returns Відписка.
 */
export function trackSelection(
  node: HTMLElement,
  report: (selection: GridSelection) => void,
): () => void {
  const onFocus = (event: Event): void => {
    const selection = selectionOfFocusEvent((event as CustomEvent<unknown>).detail);
    if (selection !== null) report(selection);
  };

  const onRange = (event: Event): void => {
    const selection = selectionOfRangeEvent((event as CustomEvent<unknown>).detail);
    if (selection !== null) report(selection);
  };

  node.addEventListener('focuscell', onFocus);
  node.addEventListener('setrange', onRange);

  return () => {
    node.removeEventListener('focuscell', onFocus);
    node.removeEventListener('setrange', onRange);
  };
}

/**
 * Обрізає виділення по справжніх межах зрізу.
 *
 * ⛔ Обов'язково: сітка могла показувати більше рядків, ніж лишилося після
 * перечитування зрізу (рядок видалили в іншій вкладці), і виділення
 * переживає перемальовування. Серіалізувати `undefined` як порожній рядок
 * означало б покласти в буфер комірки, яких немає.
 */
export function clampSelection(
  range: SelectionRange,
  rowCount: number,
  columnCount: number,
): SelectionRange | null {
  if (rowCount <= 0 || columnCount <= 0) return null;

  const fromRow = Math.min(range.fromRow, rowCount - 1);
  const fromColumn = Math.min(range.fromColumn, columnCount - 1);

  return {
    fromRow,
    toRow: Math.min(range.toRow, rowCount - 1),
    fromColumn,
    toColumn: Math.min(range.toColumn, columnCount - 1),
  };
}
