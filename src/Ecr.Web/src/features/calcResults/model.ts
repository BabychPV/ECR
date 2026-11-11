import type { CalculationYearRow, CalculationYearView } from './api';

/**
 * Модель екрана «Calculation results» (CALC-RESULTS.md): плаский перелік рядків для віртуалізованої сітки —
 * смуги «аркуш → таблиця → обладнання · методологія» і рядки виходів. Без React і без i18n: тестується чистими
 * функціями, а групи згортаються на клієнті без запиту.
 */

/** Фільтри екрана (клієнтські). */
export interface ResultFilters {
  readonly query: string;
  /** `sheetDefId` (рядком) або порожньо. */
  readonly sheet: string;
  /** Код методології; `formula` — формули шаблону; порожньо — усі. */
  readonly methodology: string;
  /** Код речовини або порожньо. */
  readonly substance: string;
  readonly problemsOnly: boolean;
}

export const NoFilters: ResultFilters = { query: '', sheet: '', methodology: '', substance: '', problemsOnly: false };

/** Код «методології» для формул шаблону у фільтрі. */
export const FormulaMethodology = 'formula';

/** Рядок плаского переліку. */
export type GridItem =
  | { readonly kind: 'band'; readonly level: 0 | 1 | 2; readonly key: string; readonly row: CalculationYearRow }
  | { readonly kind: 'output'; readonly key: string; readonly row: CalculationYearRow };

/** Ключ смуги рівня для рядка (аркуш, таблиця, обладнання · методологія). */
export function groupKeys(view: CalculationYearView, row: CalculationYearRow): [string, string, string] {
  const equipment = view.equipment[row.equipment];
  const table = view.tables[equipment?.table ?? 0];
  const sheet = `s${String(table?.sheetDefId ?? 0)}~${String(table?.sheetCopyId ?? '')}`;
  const tableKey = `${sheet}/t${String(equipment?.table ?? 0)}`;
  return [sheet, tableKey, `${tableKey}/e${String(row.equipment)}:${row.methodology === null || row.methodology === undefined ? 'f' : String(row.methodology)}`];
}

/** Індекси місяців (0…11), де вихід застарілий: є значення в місяці зі станом `outOfDate`. */
function outOfDateMonths(view: CalculationYearView, row: CalculationYearRow): number[] {
  return view.months.flatMap((month, i) => (month.runState === 'outOfDate' && row.states[i] === 'v' ? [i] : []));
}

/**
 * «Проблема» виходу: значення з застарілого місяця або пропуск усередині року (між першим і останнім місяцем зі
 * значенням є місяць із прогоном, але без числа). ⚠ Окремих станів «бракує входу» / «помилка розрахунку» сервер не
 * зберігає (у `calc.CalculationResult` лягають лише успішні числа), тож вони тут не вигадуються.
 */
export function hasProblem(view: CalculationYearView, row: CalculationYearRow): boolean {
  if (outOfDateMonths(view, row).length > 0) return true;
  const first = row.states.indexOf('v');
  const last = row.states.lastIndexOf('v');
  return first >= 0 && row.states.slice(first, last + 1).includes('a');
}

/** Текст пошуку рядка: назва обладнання, IDCode, ключ рядка. */
function searchText(view: CalculationYearView, row: CalculationYearRow, label: (row: CalculationYearRow) => string): string {
  const equipment = view.equipment[row.equipment];
  return `${label(row)} ${equipment?.idCode ?? ''} ${equipment?.rowKey ?? ''}`.toLowerCase();
}

/** Виходи, що проходять фільтри. */
export function filterRows(
  view: CalculationYearView,
  filters: ResultFilters,
  label: (row: CalculationYearRow) => string,
): CalculationYearRow[] {
  const query = filters.query.trim().toLowerCase();
  return view.rows.filter((row) => {
    const equipment = view.equipment[row.equipment];
    const table = view.tables[equipment?.table ?? 0];
    if (filters.sheet !== '' && String(table?.sheetDefId ?? '') !== filters.sheet) return false;
    if (filters.methodology !== '') {
      const code = row.methodology === null || row.methodology === undefined
        ? FormulaMethodology
        : view.methodologies[row.methodology]?.code;
      if (code !== filters.methodology) return false;
    }
    if (filters.substance !== '') {
      const code = row.substance === null || row.substance === undefined ? null : view.substances[row.substance]?.code;
      if (code !== filters.substance) return false;
    }
    if (query !== '' && !searchText(view, row, label).includes(query)) return false;
    return !filters.problemsOnly || hasProblem(view, row);
  });
}

/** Плаский перелік зі смугами; згорнута смуга ховає все, що під нею. */
export function gridItems(
  view: CalculationYearView,
  rows: readonly CalculationYearRow[],
  collapsed: ReadonlySet<string>,
): GridItem[] {
  const items: GridItem[] = [];
  let last: [string, string, string] = ['', '', ''];
  for (const row of rows) {
    const keys = groupKeys(view, row);
    if (keys[0] !== last[0]) {
      items.push({ kind: 'band', level: 0, key: keys[0], row });
      last = [keys[0], '', ''];
    }
    if (collapsed.has(keys[0])) continue;
    if (keys[1] !== last[1]) {
      items.push({ kind: 'band', level: 1, key: keys[1], row });
      last = [keys[0], keys[1], ''];
    }
    if (collapsed.has(keys[1])) continue;
    if (keys[2] !== last[2]) {
      items.push({ kind: 'band', level: 2, key: keys[2], row });
      last = keys;
    }
    if (collapsed.has(keys[2])) continue;
    items.push({ kind: 'output', key: row.id, row });
  }
  return items;
}

/** Усі ключі смуг (для «Collapse all»). */
export function allGroupKeys(view: CalculationYearView, rows: readonly CalculationYearRow[]): Set<string> {
  return new Set(rows.flatMap((row) => groupKeys(view, row)));
}

/** Лічильники місяців для підвалу. */
export function runCounts(view: CalculationYearView): { current: number; outOfDate: number; none: number } {
  const states = view.months.map((m) => m.runState);
  return {
    current: states.filter((s) => s === 'current').length,
    outOfDate: states.filter((s) => s === 'outOfDate').length,
    none: states.filter((s) => s === 'noRun' || s === 'notOpened').length,
  };
}

/** Місяць за замовчуванням для «місяця результатів» річної розкладки: останній із прогоном, інакше січень. */
export function latestRunMonth(view: CalculationYearView): number {
  let found = 1;
  for (const month of view.months) {
    if (month.runState === 'current' || month.runState === 'outOfDate') found = month.month;
  }
  return found;
}

/**
 * Адреса документа на аркуші/таблиці/місяці виходу (кнопка «Open source cell» і заголовок місяця). Параметри —
 * ті, що читає `DocumentPage` (`periodKey`, `sheet` = код аркуша або `код~копія`, `table` = код таблиці).
 */
export function documentHref(
  view: CalculationYearView,
  periodKey: number,
  row?: CalculationYearRow | undefined,
): string {
  const params = new URLSearchParams({ periodKey: String(periodKey) });
  if (row !== undefined) {
    const table = view.tables[view.equipment[row.equipment]?.table ?? 0];
    if (table !== undefined && table.tableDefId !== 0) {
      const copy = table.sheetCopyId;
      params.set('sheet', copy === null || copy === undefined ? table.sheetCode : `${table.sheetCode}~${String(copy)}`);
      params.set('table', table.tableCode);
    }
  }
  return `/documents/${String(view.documentId)}?${params.toString()}`;
}
