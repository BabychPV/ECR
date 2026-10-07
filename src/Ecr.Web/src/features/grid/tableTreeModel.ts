import type { DocumentTableDto } from '@/api/types';
import type { TableStatus } from '@/features/documents/api';

/**
 * Модель дерева таблиць аркуша (`UI-22`): стан кожної таблиці, фільтр, ключ в адресі.
 *
 * ⚠ Чисті функції без React: дерево (`TableNavigator`) і робоче місце (`SheetWorkspace`)
 * лише малюють те, що тут пораховано, а правила стану перевіряються без DOM.
 */

/**
 * Стан таблиці в дереві — рівно ті крапки, що в макеті (`docs/design/hybrid/index.html`,
 * `.dot.empty|partial|filled|error`), плюс `none`.
 *
 * ⚠ `none` — людині в цій таблиці заповнювати нічого (`inputCells === 0`: усе формульне,
 * закрите правилом періоду або динамічна без рядків). Це правило `R-13` із `summarize`:
 * така таблиця не «заповнена» і не «порожня».
 *
 * ⛔ `unknown` — статусу ще немає (запит летить або відмовив). Сірий «порожній» тут збрехав
 * би так само, як зелений стан неперевіреного документа (`A7-28`).
 */
export type TableFillState = 'unknown' | 'none' | 'empty' | 'partial' | 'filled' | 'error';

/** Рядок дерева: таблиця аркуша з її станом. */
export interface TableTreeItem {
  readonly table: DocumentTableDto;
  readonly state: TableFillState;
  /** Помилки перевірки; `null` — документ не перевіряли. */
  readonly errors: number | null;
  /** Попередження перевірки; `null` — документ не перевіряли. */
  readonly warnings: number | null;
  readonly filledCells: number | null;
  readonly inputCells: number | null;
}

/**
 * Зводить таблиці аркуша зі статусами (`GET …/tables/status`).
 *
 * ⚠ Статус адресований ОПИСОМ таблиці (`tableDefId`), а не екземпляром — так само, як
 * зауваження (`SheetTables`, перехід до комірки). Порядок — `tableOrdinal`, як у шаблоні:
 * груп у `DocumentTableDto` немає, тому дерево пласке (D15-06, картка UI-22).
 */
export function buildTableTree(
  tables: readonly DocumentTableDto[],
  statuses: readonly TableStatus[] | undefined,
): TableTreeItem[] {
  // ⛔ Ключ — аркуш І опис таблиці. Статус чужого аркуша (зокрема прихованого від цієї ролі)
  // не потрапляє в дерево, навіть якщо прийшов у відповіді: дерево знає лише таблиці, які
  // сервер віддав у `GET …/tables` для активного аркуша (вимога «прихований аркуш», P1).
  const byDef = new Map<string, TableStatus>();
  for (const status of statuses ?? []) byDef.set(statusKey(status.sheetCode, status.tableDefId), status);

  return [...tables]
    .sort((a, b) => a.tableOrdinal - b.tableOrdinal)
    .map((table) => {
      const status = statuses === undefined ? undefined : byDef.get(statusKey(table.sheetCode, table.tableDefId));

      return {
        table,
        state: fillStateOf(status),
        errors: status?.errorCount ?? null,
        warnings: status?.warningCount ?? null,
        filledCells: status?.filledCells ?? null,
        inputCells: status?.inputCells ?? null,
      };
    });
}

function statusKey(sheetCode: string, tableDefId: number): string {
  return `${sheetCode}\u0000${String(tableDefId)}`;
}

/** Стан однієї таблиці за її статусом. */
export function fillStateOf(status: TableStatus | undefined): TableFillState {
  if (status === undefined) return 'unknown';

  // ⚠ Помилка важить більше за заповненість: заповнена таблиця з помилкою — не готова.
  if (status.errorCount !== null && status.errorCount > 0) return 'error';
  if (status.inputCells <= 0) return 'none';
  if (status.filledCells <= 0) return 'empty';
  if (status.filledCells >= status.inputCells) return 'filled';

  return 'partial';
}

/** Фільтр дерева: текст і «лише з помилками» (макет: `Filter tables` + кнопка-трикутник). */
export interface TableTreeFilter {
  readonly query: string;
  readonly errorsOnly: boolean;
}

/**
 * Відбирає рядки дерева за фільтром.
 *
 * ⚠ Текст шукається в назві (мовою інтерфейсу), коді й номері таблиці без урахування
 * регістру: людина пам'ятає таблицю або назвою, або номером із паперової форми.
 */
export function filterTableTree(
  items: readonly TableTreeItem[],
  filter: TableTreeFilter,
  nameOf: (table: DocumentTableDto) => string,
): TableTreeItem[] {
  const query = filter.query.trim().toLocaleLowerCase();
  const all = items.map((item) => item.table);

  return items.filter((item) => {
    if (filter.errorsOnly && item.state !== 'error') return false;
    if (query.length === 0) return true;

    const haystack = `${String(tableNumber(item.table, all))} ${item.table.tableCode} ${nameOf(item.table)}`;
    return haystack.toLocaleLowerCase().includes(query);
  });
}

/**
 * Скільки таблиць аркуша заповнено повністю — за тим самим правилом, що `summarize` (`R-13`).
 *
 * ⚠ Лише по рядках ДЕРЕВА, тобто по таблицях активного аркуша з відповіді сервера, — не по всіх
 * аркушах документа. Помилки/попередження — `null`, якщо жодна таблиця аркуша не має числа
 * (не перевіряли або роль не бачить лічильників): «невідомо» — не «0».
 */
export function sheetProgress(items: readonly TableTreeItem[]): {
  readonly filled: number;
  readonly total: number;
  readonly errors: number | null;
  readonly warnings: number | null;
} {
  let filled = 0;
  let total = 0;
  let errors: number | null = null;
  let warnings: number | null = null;

  for (const item of items) {
    if (item.inputCells !== null && item.inputCells > 0) {
      total += 1;
      if (item.filledCells !== null && item.filledCells >= item.inputCells) filled += 1;
    }

    if (item.errors !== null) errors = (errors ?? 0) + item.errors;
    if (item.warnings !== null) warnings = (warnings ?? 0) + item.warnings;
  }

  return { filled, total, errors, warnings };
}

/** Число лічильника для показу: `null` (невідомо) — «—», ніколи не «0». */
export function countText(count: number | null): string {
  return count === null ? '—' : String(count);
}

/**
 * Ключ таблиці в адресі (`?table=`).
 *
 * ⚠ Код таблиці, а не `tableInstanceId`: екземпляр свій на кожен період (`R-A6`), і
 * посилання «той самий документ, та сама таблиця, інший місяць» з ідентифікатором
 * екземпляра вело б у нікуди. Якщо ж код на аркуші не унікальний (кілька екземплярів
 * одного опису), — `#<tableInstanceId>`, щоб другий екземпляр не був недосяжним.
 */
export function tableUrlKey(table: DocumentTableDto, tables: readonly DocumentTableDto[]): string {
  const sameCode = tables.filter((other) => other.tableCode === table.tableCode).length;

  return sameCode > 1 ? `#${String(table.tableInstanceId)}` : table.tableCode;
}

/**
 * Таблиця за ключем адреси; немає такої на аркуші (інший аркуш, застаріле посилання) —
 * перша за порядком шаблону.
 */
export function resolveTable(
  key: string | null,
  tables: readonly DocumentTableDto[],
): DocumentTableDto | undefined {
  const ordered = [...tables].sort((a, b) => a.tableOrdinal - b.tableOrdinal);

  if (key !== null && key.length > 0) {
    const found = key.startsWith('#')
      ? ordered.find((table) => String(table.tableInstanceId) === key.slice(1))
      : ordered.find((table) => table.tableCode === key);

    if (found !== undefined) return found;
  }

  return ordered[0];
}

/**
 * Номер таблиці для людини: 1, 2, 3… у порядку шаблону на аркуші.
 *
 * ⚠ Не `tableOrdinal`: сервер нумерує порядок з нуля (`TableDefHandlers`, нова таблиця —
 * `Max + 1` від 0) і допускає пропуски після перестановок, тож сирий порядок давав «0 Facility
 * details». Макет (`screen-document.js`, `tb.no`) рахує з одиниці; груп у `DocumentTableDto`
 * немає (D15-06), тому номер плаский, без «1.1».
 *
 * ⛔ Позиція — серед УСІХ таблиць аркуша, а не відфільтрованих деревом: інакше номер таблиці
 * змінювався б від пошуку.
 */
export function tableNumber(table: DocumentTableDto, tables: readonly DocumentTableDto[]): number {
  const ordered = [...tables].sort((a, b) => a.tableOrdinal - b.tableOrdinal);
  const index = ordered.findIndex((candidate) => candidate.tableInstanceId === table.tableInstanceId);

  return index < 0 ? ordered.length + 1 : index + 1;
}
