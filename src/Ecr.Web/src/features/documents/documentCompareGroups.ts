import type {
  CellChange,
  DocumentCompare,
  RowChange,
} from '@/features/documents/documentVersionsApi';

/**
 * Різниця однієї таблиці: змінені комірки й окремо — рядки, що з'явилися та
 * зникли.
 *
 * ⛔ Три переліки, а не один злитий. Додавання рядка й зміна комірки — різні
 * події з різними наслідками: змінене число можна звірити «було → стало», а
 * доданий рядок порівнювати НЕМА З ЧИМ. Звести їх в одну таблицю означало б
 * показати нові рядки як зміни з порожнього значення — тобто повідомити, що
 * там раніше стояла порожня комірка, хоча раніше там не було рядка.
 */
export interface TableDiff {
  readonly tableCode: string;
  readonly changes: readonly CellChange[];
  readonly addedRows: readonly RowChange[];
  readonly removedRows: readonly RowChange[];
}

/**
 * Розкладає різницю по таблицях.
 *
 * ⚠ Порядок таблиць — за кодом, і це рішення клієнта: сервер віддає три
 * незалежні переліки, у кожному свій порядок, і «як прийшло» означало б, що та
 * сама таблиця стоїть у різних місцях залежно від того, у якому переліку вона
 * трапилася першою.
 *
 * ⚠ Усередині таблиці порядок записів НЕ чіпається: сортувати рядки за
 * `rowKey` означало б вигадати впорядкованість, якої в даних немає (ключ рядка
 * — це бізнес-ключ, а не номер), і розійтися з порядком, у якому ті самі рядки
 * показує сітка.
 *
 * ⚠ Таблиця, у якій є ЛИШЕ додані (або лише видалені) рядки, потрапляє в
 * перелік нарівні з рештою: інакше цілком нова таблиця документа зникла б із
 * порівняння зовсім.
 */
export function groupCompareByTable(compare: DocumentCompare): TableDiff[] {
  /** Та сама різниця, поки її ще наповнюють. */
  interface Slot {
    tableCode: string;
    changes: CellChange[];
    addedRows: RowChange[];
    removedRows: RowChange[];
  }

  const byTable = new Map<string, Slot>();

  const slot = (tableCode: string): Slot => {
    const found = byTable.get(tableCode);
    if (found !== undefined) return found;

    const created: Slot = { tableCode, changes: [], addedRows: [], removedRows: [] };
    byTable.set(tableCode, created);

    return created;
  };

  for (const change of compare.changes) {
    slot(change.tableCode).changes.push(change);
  }

  for (const row of compare.addedRows) {
    slot(row.tableCode).addedRows.push(row);
  }

  for (const row of compare.removedRows) {
    slot(row.tableCode).removedRows.push(row);
  }

  return [...byTable.values()].sort((a, b) => a.tableCode.localeCompare(b.tableCode));
}

/**
 * Чи однакові версії.
 *
 * ⛔ Це НЕ те саме, що «відповіді немає» (`L10`). Відмова запиту й порожня
 * різниця — два різні твердження: перше означає «невідомо», друге — «звірено,
 * розбіжностей немає». Показати друге замість першого означало б запевнити
 * людину, що подання не змінювалося, хоча насправді його не порівнювали.
 *
 * ⚠ Усі три переліки разом, а не лише `changes`: версія, у якій РЯДОК зник, а
 * жодне число не змінилося, теж не однакова з попередньою.
 */
export function isCompareEmpty(compare: DocumentCompare): boolean {
  return (
    compare.changes.length === 0 &&
    compare.addedRows.length === 0 &&
    compare.removedRows.length === 0
  );
}

/** Що показуємо замість значення, якого немає. */
const NoValue = '—';

/**
 * Значення комірки на екрані — ЯК ПРИЙШЛО.
 *
 * ⛔ Жодного форматування числа. Сервер порівнює комірки як `decimal`
 * (`1.5` дорівнює `1.50`) і віддає обидва значення РЯДКОМ саме тому, що через
 * `number` їх вести не можна: `decimal(28,16)` не вміщається в IEEE-754. Будь-яке
 * приведення тут — `Number(value)`, `formatDecimal`, обрізання хвостових нулів —
 * зробило б із `1.50` на екрані `1.5`, тобто показало б МАСШТАБ, якого в
 * документі немає, і при цьому виглядало б як покращення.
 *
 * ⛔ І жодного власного порівняння: рішення «це зміна» ухвалив сервер. Клієнт,
 * що додасть сюди «якщо числа рівні — не показувати», заведе другу копію
 * правила порівняння decimal, і розійдеться вона мовчки.
 *
 * ⚠ Порожнє значення й `null` — одне й те саме тире: сервер прямо називає
 * відсутню й порожню комірку однаковими, тож показувати їх по-різному означало
 * б обіцяти різницю, якої немає.
 */
export function compareCellText(value: string | null): string {
  return value === null || value === '' ? NoValue : value;
}

/**
 * Як показати значення комірки: тип із зрізу подання (`oldType`/`newType`,
 * `CellChangeDto`) визначає форматування, а не сам факт наявності тексту.
 *
 * ⚠ Рішення про ФОРМАТУВАННЯ (дата, булеве) винесено з цього чистого модуля —
 * `formatDate` і переклад «так»/«ні» потребують контексту застосунку (мову,
 * локаль), якого тут немає й бути не повинно (той самий принцип, що вже
 * тримає `compareCellText` без `Intl`). Ця функція лише КЛАСИФІКУЄ значення;
 * саме форматування — на виклику, в екрані.
 */
export type CompareCellDisplay =
  | { readonly kind: 'text'; readonly text: string }
  | { readonly kind: 'date'; readonly raw: string }
  | { readonly kind: 'bool'; readonly value: boolean };

/**
 * Класифікує значення комірки за типом.
 *
 * ⛔ `ref` і `unit` НЕ несуть коду запису довідника чи символу одиниці — у
 * зрізі подання лежить сирий ідентифікатор (`SubmissionPayload.Encode`:
 * `ValueRegistryEntryId`/`ValueUnitId` рядком, перевірено
 * `DocumentCompareTests.Між_версіями_тип_входить_у_порівняння…`, де запис
 * рахується змінами `"5"` → `"6"`). Показ КОДУ вимагав би окремого
 * довідникового пошуку за ідентифікатором, якого в контракті порівняння
 * версій немає, — тому обидва типи йдуть як звичайний текст, тим самим
 * шляхом, що й число/текст без типу (`null`).
 *
 * ⚠ Зрізи, подані до `9e494c44`, мають `oldType`/`newType` = null для ВСІХ
 * типів: старі дати/bool/ref/unit пройдуть тут як звичайний текст. Маркера
 * покоління в зрізі немає — це відомий і прийнятний факт, не дефект цієї
 * функції.
 */
export function compareCellDisplay(value: string | null, type: string | null): CompareCellDisplay {
  if (value === null || value === '') return { kind: 'text', text: compareCellText(value) };
  if (type === 'date') return { kind: 'date', raw: value };
  if (type === 'bool') return { kind: 'bool', value: value === 'true' };

  return { kind: 'text', text: compareCellText(value) };
}
