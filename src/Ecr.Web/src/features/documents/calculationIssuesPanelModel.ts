import type { DocumentTableDto } from '@/api/types';
import { copyFieldsOf } from '@/features/documents/sheetCopies/sheetCopyTypes';
import { localized } from '@/shared/i18n/localized';
import type { UnmatchedRows } from './useUnmatchedRows';
import { groupUnmatchedRows } from './unmatchedRowsModel';

/**
 * ✎ B78 (людина 13.11: «не юзер френдлі, багато тексту»): ПРИЧИНА проблеми розрахунку — один рядок панелі замість
 * переліку комірок.
 *
 * - `hse400` — немає складу газу з HSE400 (формула читає довідник/документ HSE400): дія «Відкрити HSE400»;
 * - `formula` — інша власна помилка формули, згрупована за кодом і довідником формули (або колонкою);
 * - `blocked` — «не оновлено» через помилку, корінь якої в переліку не показано (інший період, закрите джерело);
 * - `unmatched` / `rejected` / `missingInputs` — рядки, не взяті в розрахунок (колишній банер «Some rows…»).
 */
export type IssueCauseKind = 'hse400' | 'formula' | 'blocked' | 'unmatched' | 'rejected' | 'missingInputs';

/** Куди веде «Показати клітинки/рядки»: перша комірка причини в сітці. */
export interface IssueTarget {
  readonly tableInstanceId: number;
  readonly rowKey: string;
  readonly columnCode: string | null;
}

/** Рядок панелі — одна причина. */
export interface IssueCause {
  readonly key: string;
  readonly kind: IssueCauseKind;
  /** Бейдж: комірки (помилки формул, з не оновленими залежними) або рядки (не взяті в розрахунок). */
  readonly count: number;
  readonly unit: 'cells' | 'rows';
  /** Код помилки (`formula`) — для людської причини (`calcErrorCause`). */
  readonly errorCode: string | null;
  /** Значення входів людськими назвами (паливо: «Diesel», не «37») — без повторів, у порядку появи. */
  readonly subjects: readonly string[];
  /** Показники — заголовки колонок з помилкою. */
  readonly indicators: readonly string[];
  /** Таблиці («Аркуш · Таблиця»). */
  readonly tables: readonly string[];
  readonly target: IssueTarget | null;
}

/** Формула читає склад HSE400 (`LAND_HSE400_COMPOSITION`, `@GCV.HSE400_…`). */
export function readsHse400(formula: string | null | undefined): boolean {
  return formula !== null && formula !== undefined && /HSE400/i.test(formula);
}

/** Перший довідник, з якого формула шукає запис (`REGFIND('КОД', …)`); `null` — немає. */
export function registryOf(formula: string | null | undefined): string | null {
  if (formula === null || formula === undefined) return null;
  const match = /REGFIND\(\s*'([^']+)'/i.exec(formula);

  return match?.[1] ?? null;
}

/**
 * ✎ B78 F13 P2: колонки-довідники, з яких формула бере ПОЛЕ запису (`REGFIELD([Land_TypeFuel], 'NAME')`): лише їхні
 * значення сервер підставляє назвою запису («Diesel»). Текстові колонки-ключі пошуку (`REGFIND('…', [TypeOfStreams])`,
 * «Makat Gas - Макатский газ») — не довідникові, у перелік палива не йдуть.
 */
export function lookupColumnsOf(formula: string | null | undefined): ReadonlySet<string> {
  const columns = new Set<string>();
  if (formula === null || formula === undefined) return columns;
  for (const match of formula.matchAll(/REGFIELD\(\s*\[([^\]]+)\]\s*,/gi)) {
    if (match[1] !== undefined) columns.add(match[1].trim().toLowerCase());
  }

  return columns;
}

/** Значення входу — назва запису довідника (не Id, який сервер не зміг назвати). */
function isLookupName(value: string | null | undefined): value is string {
  return value !== null && value !== undefined && value.trim() !== '' && !/^\d+$/.test(value.trim());
}

function tableKey(tableCode: string, copy: number | null): string {
  return copy === null ? tableCode : `${tableCode}#${String(copy)}`;
}

function tableIndex(tables: readonly DocumentTableDto[]): Map<string, DocumentTableDto> {
  const index = new Map<string, DocumentTableDto>();
  for (const table of tables) index.set(tableKey(table.tableCode, copyFieldsOf(table).sheetCopyId), table);

  return index;
}

function tableTitle(table: DocumentTableDto | undefined, fallback: string): string {
  if (table === undefined) return fallback;
  const name = localized(table.tableNameL10n) || table.tableCode;
  const sheet = localized(table.sheetNameL10n) || table.sheetCode;

  return sheet.length > 0 ? `${sheet} · ${name}` : name;
}

interface Building {
  key: string;
  kind: IssueCauseKind;
  count: number;
  unit: 'cells' | 'rows';
  errorCode: string | null;
  subjects: Set<string>;
  indicators: Set<string>;
  tables: Set<string>;
  target: IssueTarget | null;
}

function freeze(cause: Building): IssueCause {
  return {
    key: cause.key,
    kind: cause.kind,
    count: cause.count,
    unit: cause.unit,
    errorCode: cause.errorCode,
    subjects: [...cause.subjects],
    indicators: [...cause.indicators],
    tables: [...cause.tables],
    target: cause.target,
  };
}

/**
 * Причини панелі: спершу помилки формул (блокують подання), далі рядки поза розрахунком.
 *
 * ⚠ Лічильник помилок — КОМІРКИ: власна помилка (1) + її не оновлені залежні (`dependentCount`); запис «не оновлено»
 * з прихованим коренем — `dependentCount` комірок. Лічильник рядків — рядки груп `groupUnmatchedRows`.
 */
export function buildIssueCauses(rows: UnmatchedRows, tables: readonly DocumentTableDto[]): IssueCause[] {
  const index = tableIndex(tables);
  const causes = new Map<string, Building>();

  const causeOf = (key: string, kind: IssueCauseKind, unit: 'cells' | 'rows', errorCode: string | null): Building => {
    let cause = causes.get(key);
    if (cause === undefined) {
      cause = {
        key,
        kind,
        count: 0,
        unit,
        errorCode,
        subjects: new Set(),
        indicators: new Set(),
        tables: new Set(),
        target: null,
      };
      causes.set(key, cause);
    }

    return cause;
  };

  for (const error of rows.formulaErrors) {
    const table = index.get(tableKey(error.tableCode, error.sheetCopyId ?? null));
    let cause: Building;
    if (error.kind !== 'Error') {
      cause = causeOf('blocked', 'blocked', 'cells', null);
      cause.count += Math.max(1, error.dependentCount);
    } else if (readsHse400(error.formula)) {
      cause = causeOf('hse400', 'hse400', 'cells', error.errorCode);
      cause.count += 1 + error.dependentCount;
    } else {
      const scope = registryOf(error.formula) ?? error.columnCode;
      cause = causeOf(`formula:${error.errorCode}:${scope}`, 'formula', 'cells', error.errorCode);
      cause.count += 1 + error.dependentCount;
    }

    if (error.kind === 'Error') {
      // ✎ B78 F13 P2: «паливо» — лише входи з довідникових колонок формули, назвою запису; текстові входи пошуку
      // (Stream / TypeOfFuel / TypeOfStreams) не рахуються й не показуються.
      const lookups = lookupColumnsOf(error.formula);
      for (const input of error.inputs ?? []) {
        if (lookups.has(input.columnCode.toLowerCase()) && isLookupName(input.value)) cause.subjects.add(input.value);
      }
    }
    cause.indicators.add(localized(error.columnHeaderL10n) || error.columnCode);
    cause.tables.add(tableTitle(table, error.tableCode));
    if (cause.target === null && table !== undefined) {
      cause.target = { tableInstanceId: table.tableInstanceId, rowKey: error.rowKey, columnCode: error.columnCode };
    }
  }

  for (const group of groupUnmatchedRows(rows.issues, tables, rows.hints)) {
    const kind: IssueCauseKind = group.missingInputs ? 'missingInputs' : group.rejected ? 'rejected' : 'unmatched';
    const cause = causeOf(`rows:${kind}`, kind, 'rows', null);
    cause.count += group.rows.length;
    cause.tables.add(group.sheetName.length > 0 ? `${group.sheetName} · ${group.tableName}` : group.tableName);
    const first = group.rows[0];
    if (cause.target === null && group.tableInstanceId !== null && first !== undefined) {
      cause.target = {
        tableInstanceId: group.tableInstanceId,
        rowKey: first.rowKey,
        columnCode: first.missingColumns.find((column) => column.column)?.code ?? null,
      };
    }
  }

  const order: Record<IssueCauseKind, number> = {
    hse400: 0,
    formula: 1,
    blocked: 2,
    missingInputs: 3,
    rejected: 4,
    unmatched: 5,
  };

  return [...causes.values()].map(freeze).sort((a, b) => order[a.kind] - order[b.kind]);
}

/**
 * ✎ B78 F13 P3-3: сервер віддає не більше `CalculationIssuesDto.MaxFormulaErrors` записів, а `formulaErrorTotal` —
 * УСІ видимі комірки. Записи покривають комірки так: власна помилка — 1 + її не оновлені залежні, «не оновлено» з
 * прихованим коренем — `dependentCount`. Без стелі сума дорівнює `formulaErrorTotal`; менша — частину записів відкинуто,
 * і лічильники причин — нижня межа («N+»).
 */
export function formulaErrorsTruncated(rows: UnmatchedRows): boolean {
  const represented = rows.formulaErrors.reduce(
    (sum, error) => sum + (error.kind === 'Error' ? 1 + error.dependentCount : Math.max(1, error.dependentCount)),
    0,
  );

  return (rows.formulaErrorTotal ?? 0) > represented;
}

/** Перші `limit` назв через кому і «…», якщо є ще. */
export function namesOf(names: readonly string[], limit = 3): string {
  const shown = names.slice(0, limit).join(', ');

  return names.length > limit ? `${shown}…` : shown;
}
