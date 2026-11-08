import { useSyncExternalStore } from 'react';
import { viewportOfColumn } from './selection';

/**
 * Перехід від зауваження перевірки до комірки сітки (`ФВ-5.6`).
 *
 * ⛔ Панель зауважень (`ValidationPanel`) живе в чанку СТОРІНКИ, а сітки — у
 * лінивому чанку `SheetTables` (`D-132`: `RevoGrid` — 79 % ваги маршруту,
 * бюджет 250 КБ gzip). Прямого зв'язку між ними немає й бути не може: сітки
 * цільової таблиці в момент кліку може ще не існувати — вона монтується за
 * прокруткою (`SheetTables.tsx`) або лежить на іншому аркуші. Тому запит
 * переходу — у модульному сховищі, а не в пропі: сторінка кладе його сюди
 * (через `import()`, щоб цей модуль не потрапив у її чанк), `SheetTables`
 * монтує й прокручує слот таблиці, а сама сітка, дочекавшись зрізу, ставить
 * фокус на комірку й підсвічує її.
 *
 * ⚠ Запит ОДИН — останній. Новий клік замінює попередній, навіть якщо той ще
 * не доїхав: людина хоче туди, куди клацнула щойно, а не в чергу.
 */

/** Куди перейти: адреса зауваження як її віддає сервер (`ValidationFindingDto`). */
export interface CellNavigationTarget {
  readonly tableDefId: number;
  /**
   * Точний екземпляр таблиці, якщо відомий (утримана правка сітки, AN-28 P2-1);
   * без нього - перший екземпляр з `tableDefId` на аркуші.
   */
  readonly tableInstanceId?: number;
  /** `null` — зауваження до таблиці: перехід лише до неї самої. */
  readonly rowKey: string | null;
  /** `null` — зауваження до рядка: фокус на першу колонку даних. */
  readonly columnCode: string | null;
}

/**
 * Запит переходу з порядковим номером.
 *
 * ⚠ Номер — щоб повторний клік по ТОМУ САМОМУ зауваженню теж спрацював:
 * людина прокрутила геть і хоче назад. Порівняння за адресою сказало б
 * «це вже зроблено» і нічого не зробило б.
 */
export interface CellNavigationRequest extends CellNavigationTarget {
  readonly seq: number;
}

let current: CellNavigationRequest | null = null;
let sequence = 0;
const listeners = new Set<() => void>();

function notify(): void {
  for (const listener of listeners) listener();
}

/** Ставить запит переходу; попередній, недовиконаний, відкидається. */
export function requestCellNavigation(target: CellNavigationTarget): void {
  sequence += 1;
  current = {
    tableDefId: target.tableDefId,
    ...(target.tableInstanceId === undefined ? {} : { tableInstanceId: target.tableInstanceId }),
    rowKey: target.rowKey,
    columnCode: target.columnCode,
    seq: sequence,
  };
  notify();
}

/** Поточний запит; `null` — переходити нікуди. */
export function currentCellNavigation(): CellNavigationRequest | null {
  return current;
}

/**
 * Позначає запит виконаним.
 *
 * ⚠ Лише якщо це ВСЕ ЩЕ той самий запит: сітка, що завершила старий перехід
 * після нового кліку, не має права стерти новий.
 */
export function completeCellNavigation(seq: number): void {
  if (current === null || current.seq !== seq) return;

  current = null;
  notify();
}

/** Скидає невиконаний запит: сторінка пішла, і пізніший монтаж не має «стрибати». */
export function clearCellNavigation(): void {
  if (current === null) return;

  current = null;
  notify();
}

export function subscribeCellNavigation(listener: () => void): () => void {
  listeners.add(listener);

  return () => {
    listeners.delete(listener);
  };
}

/** Поточний запит переходу як стан React. */
export function useCellNavigation(): CellNavigationRequest | null {
  return useSyncExternalStore(subscribeCellNavigation, currentCellNavigation, currentCellNavigation);
}

/** Координати комірки в ВІДРЕНДЕРЕНИХ колонках сітки (`x`) і рядках (`y`). */
export interface GridCellCoordinate {
  readonly x: number;
  readonly y: number;
}

/**
 * Координати адреси в сітці; `null` — рядка в зрізі немає.
 *
 * ⛔ `x` — індекс ВІДРЕНДЕРЕНОЇ колонки, а не `slice.columns`: коли є підписи
 * рядків, сітка вставляє колонку підпису ПЕРШОЮ (`DocumentGrid.gridColumns`),
 * і без зсуву фокус став би на колонку лівіше за адресу зауваження — той самий
 * клас дефекту, що аудит §10.1 закривав для вставки (`dataColumnIndexOf`).
 *
 * ⚠ Невідома колонка (перейменована у новій версії шаблону, прихована) — не
 * відмова, а перехід до рядка: рядок відомий, і показати його чесніше, ніж
 * нічого.
 */
export function cellCoordinateOf(
  slice: {
    readonly rows: readonly { readonly rowKey: string }[];
    readonly columns: readonly { readonly code: string }[];
  },
  hasRowLabelColumn: boolean,
  rowKey: string,
  columnCode: string | null,
): GridCellCoordinate | null {
  const y = slice.rows.findIndex((row) => row.rowKey === rowKey);
  if (y < 0) return null;

  const column = columnCode === null ? -1 : slice.columns.findIndex((c) => c.code === columnCode);
  const shift = hasRowLabelColumn ? 1 : 0;

  return { x: Math.max(0, column) + shift, y };
}

/** Те з API елемента `revo-grid`, чим користується перехід. */
interface RevoGridNavigable {
  componentOnReady?: () => Promise<unknown>;
  scrollToCoordinate?: (cell: { x?: number; y?: number }) => Promise<void>;
  setCellsFocus?: (start?: GridCellCoordinate, end?: GridCellCoordinate, colType?: string) => Promise<void>;
}

/**
 * Прокручує сітку до комірки й ставить на неї фокус RevoGrid.
 *
 * ⚠ Через публічні методи веб-компонента, а не через DOM комірки: RevoGrid
 * віртуалізує рядки, і комірки за межами видимого вікна в DOM просто немає.
 *
 * ⛔ `coordinate.x` - ГЛОБАЛЬНИЙ індекс колонки сітки; `pinnedStart` - скільки колонок закріплено зліва
 * (`frozenColumns.ts`). RevoGrid ставить фокус у координатах СВОГО в'юпорту: для закріпленої колонки
 * це `colPinStart` із `x` без змін, для решти - `rgCol` із `x - pinnedStart`; прокручується лише
 * `rgCol` (закріплені колонки не прокручуються, тож для них - тільки рядок).
 *
 * @returns `false`, якщо елемента сітки в контейнері немає.
 */
export async function revealGridCell(
  container: ParentNode,
  coordinate: GridCellCoordinate,
  pinnedStart = 0,
): Promise<boolean> {
  const grid = container.querySelector('revo-grid') as (Element & RevoGridNavigable) | null;
  if (grid === null) return false;

  const target = viewportOfColumn(coordinate.x, pinnedStart);
  const local: GridCellCoordinate = { x: target.x, y: coordinate.y };

  await grid.componentOnReady?.();
  if (target.colType === 'colPinStart') {
    await grid.scrollToCoordinate?.({ y: coordinate.y });
    await grid.setCellsFocus?.(local, local, 'colPinStart');
  } else {
    await grid.scrollToCoordinate?.(local);
    await grid.setCellsFocus?.(local, local);
  }

  // ✎ fg6 F13: рядок, до якого перейшли, — у верхній третині вікна, а не на нижньому краї (людина: банер і сітка
  // «не мають займати все вікно»). Рамка фокуса RevoGrid (`revogr-focus`) з'являється після кадру.
  keepInUpperThird(() => grid.querySelector('revogr-focus') ?? grid);

  return true;
}

/** Скільки кадрів тримати рядок у верхній третині після переходу. */
const UpperThirdFrames = 40;

/**
 * Тримає елемент у верхній третині вікна ~40 кадрів після переходу.
 *
 * ⛔ Один `scrollBy` не тримається: вибір таблиці в дереві — `navigate(?table=, replace)`, нова `location.key`, і
 * глобальний `<ScrollRestoration/>` (`AppLayout`) робить `scrollTo(0, 0)` уже ПІСЛЯ фокуса (живий прохід F13: рядок
 * лишався на y≈650 із 900, на 375 px — за краєм вікна). Тому — повтор кожен кадр, доки людина сама не прокрутила
 * (колесо, дотик, клавіша, клік) — той самий прийом, що відновлення прокрутки при зміні періоду (`DocumentPage`).
 */
export function keepInUpperThird(target: () => Element): void {
  let frames = 0;
  let frame = 0;
  let stopped = false;
  const userScroll = ['wheel', 'touchstart', 'keydown', 'mousedown'] as const;
  const stop = (): void => {
    stopped = true;
    window.cancelAnimationFrame(frame);
    for (const type of userScroll) window.removeEventListener(type, stop);
  };
  for (const type of userScroll) window.addEventListener(type, stop, { passive: true, once: true });
  const tick = (): void => {
    if (stopped) return;
    scrollIntoUpperThird(target());
    frames += 1;
    if (frames < UpperThirdFrames) frame = window.requestAnimationFrame(tick);
    else stop();
  };
  frame = window.requestAnimationFrame(tick);
}

/**
 * Прокручує СТОРІНКУ так, щоб верх елемента став на третину висоти вікна, — якщо він зараз не у верхніх 40 %
 * (або вище за край). Уже видимий угорі елемент не смикається.
 */
export function scrollIntoUpperThird(element: Element, win: Window = window): void {
  const height = win.innerHeight;
  if (height <= 0) return;

  const top = element.getBoundingClientRect().top;
  if (top >= 0 && top <= height * 0.4) return;

  win.scrollBy({ top: top - height / 3 });
}

/** Скільки тримається підсвітка цільової комірки. */
export const NavigationHighlightMs = 4000;
