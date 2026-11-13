import type {
  CalculationYearColumn,
  CalculationYearEquipment,
  CalculationYearMethodology,
  CalculationYearMonth,
  CalculationYearRows,
  CalculationYearSubstance,
  CalculationYearTable,
  CalculationYearUnit,
} from './api';

/**
 * Модель екрана «Calculation results» (CALC-RESULTS.md): плаский перелік рядків для віртуалізованої сітки — смуги
 * «аркуш → таблиця → обладнання · методологія», рядки виходів і рядок «ще» таблиці, сторінки якої завантажено не всі.
 * Без React і без i18n: тестується чистими функціями.
 *
 * ⚠ Таблиці приходять зі зведення (лічильники), їхні рядки — сторінками лише для розгорнутих таблиць; фільтри застосовує
 * сервер (одне правило для зведення, сторінок і експорту).
 */

/** Вихід сторінки з розв'язаними посиланнями на довідники СВОЄЇ сторінки. */
export interface ResultRow {
  readonly id: string;
  readonly table: CalculationYearTable;
  readonly equipment: CalculationYearEquipment;
  readonly methodology: CalculationYearMethodology | null;
  readonly substance: CalculationYearSubstance | null;
  readonly unit: CalculationYearUnit | null;
  readonly column: CalculationYearColumn | null;
  readonly outputCode: string;
  readonly values: readonly unknown[];
  readonly states: string;
  readonly yearTotal: unknown;
  readonly monthsIncluded: number;
}

/** Рядок плаского переліку. */
export type GridItem =
  | { readonly kind: 'sheet'; readonly key: string; readonly table: CalculationYearTable }
  | { readonly kind: 'table'; readonly key: string; readonly table: CalculationYearTable }
  | { readonly kind: 'group'; readonly key: string; readonly row: ResultRow }
  | { readonly kind: 'output'; readonly key: string; readonly row: ResultRow }
  | { readonly kind: 'more'; readonly key: string; readonly table: CalculationYearTable; readonly loaded: number; readonly total: number };

function at<T>(list: readonly T[], index: number | null | undefined): T | null {
  return index === null || index === undefined ? null : (list[index] ?? null);
}

/** Рядки сторінки з розв'язаними індексами. */
export function resolvePage(table: CalculationYearTable, page: CalculationYearRows): ResultRow[] {
  return page.rows.flatMap((row) => {
    const equipment = page.equipment[row.equipment];
    if (equipment === undefined) return [];
    return [
      {
        id: row.id,
        table,
        equipment,
        methodology: at(page.methodologies, row.methodology),
        substance: at(page.substances, row.substance),
        unit: at(page.units, row.unit),
        column: at(page.columns, row.column),
        outputCode: row.outputCode,
        values: row.values,
        states: row.states,
        yearTotal: row.yearTotal,
        monthsIncluded: row.monthsIncluded,
      },
    ];
  });
}

/**
 * Курсори сторінок таблиці підряд від початку: перша — `null`, кожна наступна — `nextCursor` попередньої. Сторінка
 * `i` запитується, лише коли сторінку `i − 1` прочитано і вона не остання (курсор знає лише сервер; зсуву немає —
 * він пропускав би рядки на змінному наборі). `want` — скільки сторінок хоче екран, `loaded(i)` — прочитана сторінка.
 */
export function pageCursors(
  want: number,
  loaded: (index: number) => Pick<CalculationYearRows, 'nextCursor'> | undefined,
): (string | null)[] {
  const cursors: (string | null)[] = [];
  let cursor: string | null = null;
  for (let i = 0; i < want; i++) {
    cursors.push(cursor);
    const next = loaded(i)?.nextCursor;
    if (next === undefined || next === null) break;
    cursor = next;
  }
  return cursors;
}

/** Ключ смуги аркуша. */
export function sheetKey(table: CalculationYearTable): string {
  return `s${String(table.sheetDefId)}~${String(table.sheetCopyId ?? '')}`;
}

/** Ключ смуги «обладнання · методологія» (у межах таблиці). */
export function groupKey(row: ResultRow): string {
  return `${row.table.key}/e${row.equipment.rowKey ?? ''}:${row.methodology === null ? 'f' : String(row.methodology.methodologyId)}`;
}

/** Що вже завантажено для таблиці: рядки підряд від початку і скільки їх усього (з фільтрами). */
export interface LoadedTable {
  readonly rows: readonly ResultRow[];
  readonly total: number;
}

/**
 * Плаский перелік: аркуш → таблиця (згорнута, доки її не розгорнули) → обладнання · методологія → виходи → «ще».
 * `collapsed` — згорнуті аркуші й групи обладнання; `open` — розгорнуті таблиці (лише їхні рядки читаються).
 */
export function gridItems(
  tables: readonly CalculationYearTable[],
  loaded: ReadonlyMap<string, LoadedTable>,
  collapsed: ReadonlySet<string>,
  open: ReadonlySet<string>,
): GridItem[] {
  const items: GridItem[] = [];
  let lastSheet = '';
  for (const table of tables) {
    const sheet = sheetKey(table);
    if (sheet !== lastSheet) {
      items.push({ kind: 'sheet', key: sheet, table });
      lastSheet = sheet;
    }
    if (collapsed.has(sheet)) continue;
    items.push({ kind: 'table', key: table.key, table });
    if (!open.has(table.key)) continue;

    const data = loaded.get(table.key);
    let lastGroup = '';
    for (const row of data?.rows ?? []) {
      const group = groupKey(row);
      if (group !== lastGroup) {
        items.push({ kind: 'group', key: group, row });
        lastGroup = group;
      }
      if (collapsed.has(group)) continue;
      items.push({ kind: 'output', key: row.id, row });
    }
    const count = data?.rows.length ?? 0;
    const total = data?.total ?? table.matched;
    if (data === undefined || count < total) {
      items.push({ kind: 'more', key: `${table.key}/more`, table, loaded: count, total });
    }
  }
  return items;
}

/** Лічильники місяців для підвалу. */
export function runCounts(months: readonly CalculationYearMonth[]): { current: number; outOfDate: number; none: number } {
  const states = months.map((m) => m.runState);
  return {
    current: states.filter((s) => s === 'current').length,
    outOfDate: states.filter((s) => s === 'outOfDate').length,
    none: states.filter((s) => s === 'noRun' || s === 'notOpened').length,
  };
}

/** Місяць за замовчуванням для «місяця результатів» річної розкладки: останній із прогоном, інакше січень. */
export function latestRunMonth(months: readonly CalculationYearMonth[]): number {
  let found = 1;
  for (const month of months) {
    if (month.runState === 'current' || month.runState === 'outOfDate') found = month.month;
  }
  return found;
}

/**
 * Адреса документа на аркуші/таблиці/місяці виходу (кнопка «Open source cell» і заголовок місяця). Параметри —
 * ті, що читає `DocumentPage` (`periodKey`, `sheet` = код аркуша або `код~копія`, `table` = код таблиці).
 */
export function documentHref(documentId: number, periodKey: number, row?: ResultRow | undefined): string {
  const params = new URLSearchParams({ periodKey: String(periodKey) });
  const table = row?.table;
  if (table !== undefined && table.tableDefId !== 0) {
    const copy = table.sheetCopyId;
    params.set('sheet', copy === null || copy === undefined ? table.sheetCode : `${table.sheetCode}~${String(copy)}`);
    params.set('table', table.tableCode);
  }
  return `/documents/${String(documentId)}?${params.toString()}`;
}
