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
interface SelectionRange {
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
function isMainRowViewport(detail: { rowType?: unknown }): boolean {
  return detail.rowType === undefined || detail.rowType === 'rgRow';
}

/**
 * Індекс колонки в `x` події -> ГЛОБАЛЬНИЙ індекс колонки сітки.
 *
 * ⛔ Закріплені колонки (`pin: 'colPinStart'`, `frozenColumns.ts`) живуть у власному в'юпорті, і `x`
 * події відлічується від ПОЧАТКУ ЦЬОГО в'юпорту: `colPinStart` -> `x`, `rgCol` -> `x + pinnedStart`
 * (закріплені колонки - перші, тож прокручувана частина починається після них). Без зсуву клік по
 * третій прокручуваній колонці при двох закріплених вказував би на першу - і вставка лягала б у
 * колонки лівіше за видимі.
 *
 * ⚠ `colPinEnd` не використовується: подія з нього - `null`, як і раніше.
 *
 * @returns `null`, якщо в'юпорт не підтримується або `x` не індекс.
 */
export function globalColumnOf(colType: unknown, x: unknown, pinnedStart: number): number | null {
  const index = indexOf(x);
  if (index === null) return null;
  if (colType === 'colPinStart') return index;
  if (colType === undefined || colType === 'rgCol') return index + Math.max(0, pinnedStart);

  return null;
}

/** Куди йти за глобальним індексом колонки: в'юпорт і `x` усередині нього. */
export function viewportOfColumn(
  globalColumn: number,
  pinnedStart: number,
): { readonly colType: 'colPinStart' | 'rgCol'; readonly x: number } {
  const pinned = Math.max(0, pinnedStart);

  return globalColumn < pinned
    ? { colType: 'colPinStart', x: globalColumn }
    : { colType: 'rgCol', x: globalColumn - pinned };
}

/**
 * В'юпорт колонок, з якого прийшла подія, за її шляхом у DOM (`colPinStart`/`rgCol`/`colPinEnd`).
 *
 * ⚠ `setrange` не несе `colType`, зате випромінюється з `revogr-overlay-selection` ВСЕРЕДИНІ
 * `revogr-viewport-scroll.<colType>`. `null` - шлях недоступний (закрите тіньове дерево) або
 * не містить в'юпорту; тоді трекер бере в'юпорт останнього `focuscell`.
 */
export function viewportOfEvent(event: Event): 'colPinStart' | 'rgCol' | 'colPinEnd' | null {
  for (const node of event.composedPath()) {
    if (!(node instanceof Element) || node.localName !== 'revogr-viewport-scroll') continue;

    for (const type of ['colPinStart', 'rgCol', 'colPinEnd'] as const) {
      if (node.classList.contains(type)) return type;
    }
  }

  return null;
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
export function selectionOfFocusEvent(detail: unknown, pinnedStart = 0): GridSelection | null {
  if (typeof detail !== 'object' || detail === null) return null;

  const event = detail as {
    rowType?: unknown;
    colType?: unknown;
    focus?: { x?: unknown; y?: unknown };
    end?: { x?: unknown; y?: unknown };
  };

  if (!isMainRowViewport(event)) return null;

  const x = globalColumnOf(event.colType, event.focus?.x, pinnedStart);
  const y = indexOf(event.focus?.y);
  if (x === null || y === null) return null;

  // ⚠ `end` — протилежний кут виділення; його може не бути (звичайний клік),
  // і тоді діапазон вироджується у саму комірку. Діапазон RevoGrid не перетинає межу
  // в'юпортів (в кожного в'юпорту своє виділення), тож `end` - у тому ж в'юпорті, що й `focus`.
  const x1 = globalColumnOf(event.colType, event.end?.x, pinnedStart) ?? x;
  const y1 = indexOf(event.end?.y) ?? y;
  const range = rangeOf(x, y, x1, y1);

  return { anchor: { rowIndex: range.fromRow, columnIndex: range.fromColumn }, range };
}

/**
 * Розбирає `detail` події `setrange` (`RangeArea & { type }`).
 *
 * ⚠ `detail` НЕ несе `colType` (лише `type` - тип РЯДКІВ), тож в'юпорт передається окремо: трекер
 * бере його з шляху події (`viewportOfEvent`).
 *
 * @returns `null`, якщо подія не несе повного прямокутника.
 */
export function selectionOfRangeEvent(
  detail: unknown,
  pinnedStart = 0,
  colType: unknown = 'rgCol',
): GridSelection | null {
  if (typeof detail !== 'object' || detail === null) return null;

  const event = detail as { x?: unknown; y?: unknown; x1?: unknown; y1?: unknown };

  const x = globalColumnOf(colType, event.x, pinnedStart);
  const y = indexOf(event.y);
  const x1 = globalColumnOf(colType, event.x1, pinnedStart);
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
 * @param pinnedStart Скільки колонок закріплено зліва НА МОМЕНТ події (читається щоразу: вибір
 * користувача змінюється, а слухачі ставляться один раз).
 * @returns Відписка.
 */
export function trackSelection(
  node: HTMLElement,
  report: (selection: GridSelection) => void,
  pinnedStart: () => number = () => 0,
): () => void {
  // Останній в'юпорт, у якому був фокус: запасний, якщо шлях події `setrange` його не назвав.
  let lastColType: unknown = 'rgCol';

  const onFocus = (event: Event): void => {
    const detail = (event as CustomEvent<unknown>).detail as { colType?: unknown } | null;
    if (detail !== null && typeof detail === 'object' && detail.colType !== undefined) lastColType = detail.colType;

    const selection = selectionOfFocusEvent(detail, pinnedStart());
    if (selection !== null) report(selection);
  };

  const onRange = (event: Event): void => {
    const colType = viewportOfEvent(event) ?? lastColType;
    const selection = selectionOfRangeEvent((event as CustomEvent<unknown>).detail, pinnedStart(), colType);
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
 * Комірка, у якій ЗАРАЗ фокус, — у термінах індексів того, що отримала сітка
 * (`UI-08`, рядок формули).
 *
 * ⛔ Це не `GridSelection.anchor`. Якір — верхній лівий кут виділення, бо від
 * нього розкладається вставка; рядок формули ж показує АКТИВНУ комірку, як в
 * Excel. Виділяючи Shift+↑ знизу вгору, оператор лишає фокус у нижньому кінці
 * діапазону — якір там указує на іншу комірку, і рядок формули показував би
 * вираз не тієї колонки, у якій стоїть курсор.
 *
 * ⚠ `columnIndex` — індекс серед колонок, які ОТРИМАЛА сітка, а не серед
 * `slice.columns`: коли є колонка підпису рядка, вона перша й зсуває решту на
 * одиницю. Зіставлення індексу з колонкою зрізу робить `formulaBar.ts` за
 * самим масивом `ColumnRegular[]`, а не арифметикою по зсуву.
 */
export interface FocusedCell {
  readonly rowIndex: number;
  readonly columnIndex: number;
}

/**
 * Розбирає `detail` події `focuscell` у комірку фокуса.
 *
 * ⚠ Свідомо окремо від `selectionOfFocusEvent`, а не нове поле в
 * `GridSelection`: те значення читають `onPaste`/`onCopy` із `ref`, і воно
 * навмисно НЕ викликає перемальовування. Фокус, навпаки, має доїжджати до
 * React — але лише до рядка формули (`focusStore.ts`), не до всієї сітки.
 *
 * @returns `null`, якщо подія не про тіло таблиці або не несе координат.
 */
export function focusedCellOfEvent(detail: unknown, pinnedStart = 0): FocusedCell | null {
  if (typeof detail !== 'object' || detail === null) return null;

  const event = detail as { rowType?: unknown; colType?: unknown; focus?: { x?: unknown; y?: unknown } };

  if (!isMainRowViewport(event)) return null;

  const columnIndex = globalColumnOf(event.colType, event.focus?.x, pinnedStart);
  const rowIndex = indexOf(event.focus?.y);
  if (columnIndex === null || rowIndex === null) return null;

  return { rowIndex, columnIndex };
}

/**
 * Підписується на зміну комірки фокуса в межах контейнера.
 *
 * ⛔ Лише `focuscell`. `setrange` координат фокуса не несе взагалі
 * (`{x, y, x1, y1}` — це прямокутник), тож прийняти його кут за активну
 * комірку означало б посунути рядок формули туди, куди курсор не ставав.
 *
 * @returns Відписка.
 */
export function trackFocusedCell(
  node: HTMLElement,
  report: (cell: FocusedCell) => void,
  pinnedStart: () => number = () => 0,
): () => void {
  const onFocus = (event: Event): void => {
    const cell = focusedCellOfEvent((event as CustomEvent<unknown>).detail, pinnedStart());
    if (cell !== null) report(cell);
  };

  node.addEventListener('focuscell', onFocus);

  return () => {
    node.removeEventListener('focuscell', onFocus);
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
