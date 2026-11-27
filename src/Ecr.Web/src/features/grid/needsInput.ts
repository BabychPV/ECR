import type { RequiredInputCell } from '@/api/client';
import { t } from '@/shared/i18n';
import { cellKey } from './permissions';

/**
 * ✎ DOCUX L2 (план `docs/build/UX-REVIEW-document-screen.md` §4): ОДНА модель «тут потрібне значення» для сітки.
 *
 * Людина (26.11): старий «!» у куті «ледь видно і незрозуміло» — він з'являвся лише після збереження й лише для входів
 * методологій, а порожня обов'язкова комірка шаблону не позначалась зовсім. Тепер модель зводить ТРИ джерела, відомі
 * клієнту, одразу при відкритті аркуша:
 *
 * | Джерело | Рівень | Звідки |
 * |---|---|---|
 * | колонка шаблону `isRequired`, комірка порожня | `block` (без неї аркуш не подати, `ECR-CELL-0422`) | зріз |
 * | вхід методології (`ECR-CALC-0437`) з відповіді збереження | `block` / `warn`, як сказав сервер | відповідь PATCH |
 * | «Missing input» з `calculation-issues` | `warn` | перелік проблем розрахунку |
 *
 * ⚠ Значення — З ПОТОЧНИХ даних сітки (незбережені правки враховано): набрали значення — позначка зникає одразу
 * (BUG-10: лічильник і позначка більше не розходяться).
 * ⚠ Q4 (рішення людини): «Required» — у рядках, де вже є хоч одне значення, і в наперед заданих рядках шаблону; порожні
 * «запасні» рядки, додані людиною (`Dynamic`/`Mixed`), — без позначок. Лише редаговні комірки; рядок підсумків — ні.
 * ⚠ `isRequiredByMethodology` — ознака КОЛОНКИ («обов'язкова для частини рядків»), а не кожної комірки: сама по собі
 * позначки не ставить (BUG-13); який рядок потребує входу, каже сервер (`ECR-CALC-0437`, `MissingInputs`).
 */
export type NeedsLevel = 'block' | 'warn';

export type NeedsSource = 'template' | 'methodology' | 'calcIssue';

export interface NeedsInputCell {
  readonly rowKey: string;
  readonly columnCode: string;
  readonly level: NeedsLevel;
  readonly source: NeedsSource;
  /** Коди методологій, яким потрібен вхід (порожньо — шаблон або невідомо). */
  readonly methodologies: readonly string[];
  /** Індекс рядка в зрізі (порядок обходу F8). */
  readonly rowIndex: number;
  /** Індекс колонки даних у зрізі. */
  readonly columnIndex: number;
  /** ✎ DOCUX L9: місяць-власник колонки (річна розкладка); `null`/немає — колонка без місяця. */
  readonly ownerPeriodKey?: number | null;
}

/** Колонка зрізу — лише те, що потрібно моделі. */
export interface NeedsColumn {
  readonly code: string;
  readonly isRequired: boolean;
}

/** Рядок зрізу — ключ і вид (наперед заданий чи доданий людиною). */
export interface NeedsRow {
  readonly rowKey: string;
  readonly rowKind?: string | null;
}

/** Модель рядка сітки — значення за кодом колонки, з незбереженими правками. */
export type NeedsShownRow = { readonly __rowKey: string } & Readonly<Record<string, unknown>>;

/** Пропущений вхід із переліку проблем розрахунку (`CalculationIssueDto`, вид `MissingInputs`). */
export interface MissingInputIssue {
  readonly rowKey: string;
  readonly kind: string;
  readonly methodologyCode?: string | null;
  readonly columns?: readonly { readonly code: string }[] | null;
}

export interface NeedsInputSources {
  readonly columns: readonly NeedsColumn[];
  readonly rows: readonly NeedsRow[];
  /** Моделі рядків сітки в тому самому порядку, що й `rows`; `null` — даних ще немає. */
  readonly shown: readonly NeedsShownRow[] | null;
  readonly blocked: readonly RequiredInputCell[];
  readonly warnings: readonly RequiredInputCell[];
  readonly issues: readonly MissingInputIssue[];
  /** ✎ Контракт doc-ux-server: порожні обов'язкові входи таблиці по ВСІХ рядках (`GET …/required-inputs`). */
  readonly server?: readonly { readonly rowKey: string; readonly columnCode: string; readonly level: string; readonly methodologies: readonly string[] }[];
  /** Чи людина може писати в комірку (права, стан аркуша, формула). */
  readonly editable: (rowKey: string, columnCode: string) => boolean;
}

export function isFilledValue(value: unknown): boolean {
  return value !== null && value !== undefined && !(typeof value === 'string' && value.trim() === '');
}

/** Рядок, доданий людиною (`Dynamic`/`Mixed`), — «запасний», поки в ньому немає жодного значення. */
function isUserRow(row: NeedsRow): boolean {
  return row.rowKind === 'Dynamic' || row.rowKind === 'Mixed';
}

/** Рівень `block` сильніший за `warn`; методологія з переліком назв — повніша за порожній. */
function merge(known: NeedsInputCell, next: NeedsInputCell): NeedsInputCell {
  const level: NeedsLevel = known.level === 'block' || next.level === 'block' ? 'block' : 'warn';
  const methodologies = [...known.methodologies];
  for (const code of next.methodologies) if (!methodologies.includes(code)) methodologies.push(code);
  const source = known.source === 'template' ? 'template' : next.source === 'template' ? 'template' : known.source;

  return { ...known, level, source, methodologies };
}

/** Комірки, що потребують значення, у порядку обходу (рядок за рядком, колонка за колонкою). */
export function needsInputCells(sources: NeedsInputSources): NeedsInputCell[] {
  const { columns, rows, shown } = sources;
  if (shown === null) return [];

  const rowIndex = new Map(rows.map((row, index) => [row.rowKey, index]));
  const columnIndex = new Map(columns.map((column, index) => [column.code, index]));
  const shownByKey = new Map(shown.map((row) => [row.__rowKey, row]));
  const byCell = new Map<string, NeedsInputCell>();

  const add = (cell: Omit<NeedsInputCell, 'rowIndex' | 'columnIndex'>): void => {
    const y = rowIndex.get(cell.rowKey);
    const x = columnIndex.get(cell.columnCode);
    if (y === undefined || x === undefined) return;
    const row = shownByKey.get(cell.rowKey);
    if (row === undefined || isFilledValue(row[cell.columnCode])) return;
    if (!sources.editable(cell.rowKey, cell.columnCode)) return;

    const full: NeedsInputCell = { ...cell, rowIndex: y, columnIndex: x };
    const key = `${cell.rowKey}\u0000${cell.columnCode}`;
    const known = byCell.get(key);
    byCell.set(key, known === undefined ? full : merge(known, full));
  };

  // 1. Колонки шаблону `isRequired` — наперед задані рядки і рядки, де вже є значення (Q4).
  const required = columns.filter((column) => column.isRequired);
  if (required.length > 0) {
    rows.forEach((row) => {
      const model = shownByKey.get(row.rowKey);
      if (model === undefined) return;
      if (isUserRow(row) && !columns.some((column) => isFilledValue(model[column.code]))) return;
      for (const column of required) {
        add({ rowKey: row.rowKey, columnCode: column.code, level: 'block', source: 'template', methodologies: [] });
      }
    });
  }

  // 2. Входи методологій із відповіді збереження (`ECR-CALC-0437`).
  for (const cell of sources.blocked) {
    add({ rowKey: cell.rowKey, columnCode: cell.columnCode, level: 'block', source: 'methodology', methodologies: cell.methodologies ?? [] });
  }
  for (const cell of sources.warnings) {
    add({ rowKey: cell.rowKey, columnCode: cell.columnCode, level: 'warn', source: 'methodology', methodologies: cell.methodologies ?? [] });
  }

  // 2б. Перелік сервера по всіх рядках (і тих, яких не правили): Block / Warn, як налаштовано у версії методології.
  for (const cell of sources.server ?? []) {
    add({ rowKey: cell.rowKey, columnCode: cell.columnCode, level: cell.level === 'block' ? 'block' : 'warn', source: 'methodology', methodologies: cell.methodologies });
  }

  // 3. «Missing input» з переліку проблем розрахунку.
  for (const issue of sources.issues) {
    if (issue.kind !== 'MissingInputs') continue;
    const methodologies = issue.methodologyCode == null ? [] : [issue.methodologyCode];
    for (const column of issue.columns ?? []) {
      add({ rowKey: issue.rowKey, columnCode: column.code, level: 'warn', source: 'calcIssue', methodologies });
    }
  }

  return [...byCell.values()].sort((a, b) => a.rowIndex - b.rowIndex || a.columnIndex - b.columnIndex);
}

/** Пояснення комірки словами (підказка, рядок формул, живий регіон) — ключі літералами (сторож каталогу). */
export function needsInputHint(cell: NeedsInputCell): string {
  if (cell.source === 'template') return t('grid.needsInput.hintTemplate');
  // ✎ DOCUX (живий прохід 27.11): попередження (амбра) — без «…і аркуш не подати»: подання воно не блокує. Напр. порожній
  // ключ правил методології («Type of fuel»): рядок не рахується, але подати можна.
  if (cell.level === 'warn') {
    return cell.methodologies.length === 0
      ? t('grid.legend.requiredWarn')
      : t('grid.needsInput.hintMethodologyWarn', { methodologies: cell.methodologies.join(', ') });
  }
  if (cell.methodologies.length === 0) return t('grid.needsInput.hintMethodologyUnnamed');

  return t('grid.needsInput.hintMethodology', { methodologies: cell.methodologies.join(', ') });
}

/** Ключ комірки моделі — той самий, що `cellKey` сітки (`permissions.ts`). */
export const needsKey = cellKey;

/** Мапи для `gridColumns`: ключ комірки → пояснення, окремо за рівнем. */
export function needsInputMaps(cells: readonly NeedsInputCell[]): {
  readonly blocked: ReadonlyMap<string, string>;
  readonly warning: ReadonlyMap<string, string>;
} {
  const blocked = new Map<string, string>();
  const warning = new Map<string, string>();
  for (const cell of cells) {
    (cell.level === 'block' ? blocked : warning).set(needsKey(cell.rowKey, cell.columnCode), needsInputHint(cell));
  }

  return { blocked, warning };
}

/** Скільки порожніх обов'язкових комірок у кожній колонці (лічильник поруч із зірочкою заголовка). */
export function needsInputByColumn(cells: readonly NeedsInputCell[]): ReadonlyMap<string, number> {
  const counts = new Map<string, number>();
  for (const cell of cells) counts.set(cell.columnCode, (counts.get(cell.columnCode) ?? 0) + 1);

  return counts;
}
