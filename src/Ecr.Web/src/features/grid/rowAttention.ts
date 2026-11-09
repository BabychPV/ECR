import type { TableSliceDto } from '@/api/types';
import { formatPeriodKey } from '@/shared/format/periodKey';
import { t } from '@/shared/i18n';
import { cellKey } from './permissions';
import type { PeriodRowFindings, RowAttentionItem, TableAttention } from './rowAttentionStore';

export {
  publishTableAttention,
  requestAttentionFocus,
  useRowFindings,
  type RowAttentionItem,
  type TableAttention,
} from './rowAttentionStore';

/**
 * ✎ UAT 08.11 (Water Report, рядок «Sea Water Intake»; людина: «чому строка червона і зі знаками оклику в
 * кутку?», «треба перевірити список issues він мені здається не працює»).
 *
 * Червоний рядок зі знаком «!» у куті кожної комірки — стан `orphaned` (`cell-states.css`): нічна перевірка
 * посилань (`OrphanScanPlan`) позначила рядок, бо його значення довідника (тут — «Permit number») не дійсне
 * в цьому періоді. Сітка показувала ЛИШЕ колір і знак: ні підказки в комірці, ні слова біля підпису рядка, ні
 * пункту в Issues (Issues — результати перевірки правил, а ця позначка не з них). Місяці рядка закриті
 * (`PeriodClosed`), і ще й тому оператор не міг нічого змінити — про це теж ніде не було сказано.
 *
 * Тут — причина СЛОВАМИ для рядка (поруч із підписом) і стан таблиці для спільного сховища
 * (`rowAttentionStore.ts`), з якого її читають Issues і рядок стану збереження.
 */

/**
 * Перелік назв колонок: кожна серія з трьох і більше колонок, що йдуть поспіль у зрізі, — діапазоном
 * («January–August»), решта — через кому. Порядок — порядок колонок зрізу.
 */
export function describeColumns(slice: TableSliceDto, codes: ReadonlySet<string>): string | null {
  const indices = slice.columns.flatMap((column, index) => (codes.has(column.code) ? [index] : []));
  if (indices.length === 0) return null;

  const nameAt = (index: number): string => slice.columns[index]?.header || slice.columns[index]?.code || '';
  const runs: number[][] = [];
  for (const index of indices) {
    const last = runs[runs.length - 1];
    if (last !== undefined && last[last.length - 1] === index - 1) last.push(index);
    else runs.push([index]);
  }

  return runs
    .map((run) =>
      run.length > 2 ? `${nameAt(run[0] ?? 0)}–${nameAt(run[run.length - 1] ?? 0)}` : run.map(nameAt).join(', '),
    )
    .join(', ');
}

/** Колонки зрізу, у яких сервер відмовив хоч одній комірці (рядка `rowKey`, якщо задано) через закритий період. */
export function lockedColumnsOf(slice: TableSliceDto, rowKey?: string): Set<string> {
  const rows = rowKey === undefined ? slice.rows : slice.rows.filter((row) => row.rowKey === rowKey);
  const codes = new Set<string>();
  for (const column of slice.columns) {
    if (rows.some((row) => slice.cellPermissions[cellKey(row.rowKey, column.code)] === 'PeriodClosed')) codes.add(column.code);
  }

  return codes;
}

/**
 * ✎ UAT 08.11 (F13-p209 P2-2): ЯКЕ значення довідника рядка недійсне і чому — назва запису і його чинність
 * («KZ35VTE00340032, чинний 01.03.2026 – …»), а не лише «значення недійсне». Формулювання — як у правила (h)
 * (`err.ECR-CELL-4223.permitNotCoveringMonths`: «permit {permit} is valid {validity} and does not cover …»).
 */
export interface RowLookupProblem {
  /** Колонка — ціль «Permits» (шапка типу дозволу або група): текст про дозвіл. */
  readonly permit: boolean;
  /** Назва колонки (для не-дозволів). */
  readonly column: string;
  /** Назва запису. */
  readonly value: string;
  /** Чинність `dd.MM.yyyy – dd.MM.yyyy` (`…` — без межі). */
  readonly validity: string;
}

/** Що відомо про запис довідника: назва і чинність (`validTo` — виключна межа, як на сервері). */
export interface LookupEntryValidity {
  readonly label: string;
  readonly validFrom: string | null;
  readonly validTo: string | null;
}

function ddmmyyyy(iso: string): string {
  const [year, month, day] = iso.slice(0, 10).split('-');

  return `${day ?? ''}.${month ?? ''}.${year ?? ''}`;
}

/** Чинність так само, як `PermitMonthCoverage.DescribeValidity` сервера: `validTo` виключна, тож показ — день перед нею. */
export function describeValidity(validFrom: string | null, validTo: string | null): string {
  const from = validFrom === null ? '…' : ddmmyyyy(validFrom);
  let to = '…';
  if (validTo !== null) {
    const date = new Date(`${validTo.slice(0, 10)}T00:00:00Z`);
    date.setUTCDate(date.getUTCDate() - 1);
    to = ddmmyyyy(date.toISOString());
  }

  return `${from} – ${to}`;
}

/** Чи запис чинний хоч один день місяця `periodKey` (`YYYYMM`). */
export function coversPeriod(entry: Pick<LookupEntryValidity, 'validFrom' | 'validTo'>, periodKey: number): boolean {
  const year = Math.floor(periodKey / 100);
  const month = periodKey % 100;
  const mm = String(month).padStart(2, '0');
  const first = `${String(year)}-${mm}-01`;
  const last = `${String(year)}-${mm}-${String(new Date(Date.UTC(year, month, 0)).getUTCDate()).padStart(2, '0')}`;
  const from = entry.validFrom?.slice(0, 10) ?? null;
  const to = entry.validTo?.slice(0, 10) ?? null;

  return (from === null || from <= last) && (to === null || to > first);
}

/**
 * Для кожного рядка з утраченим посиланням — перше значення довідника, чинність якого не покриває період зрізу.
 * `entryOf` дає запис за довідником і id (перелік на дату або добраний закритий); невідомий запис — пропуск
 * (тоді лишається загальний текст).
 */
export function rowLookupProblemsOf(
  slice: TableSliceDto,
  entryOf: (registryId: number, id: number) => LookupEntryValidity | undefined,
  isPermitRegistry: (registryId: number) => boolean,
): ReadonlyMap<string, RowLookupProblem> {
  const problems = new Map<string, RowLookupProblem>();
  const lookups = slice.columns.filter((column) => column.dataType === 'Lookup' && column.lookupRegistryDefId !== null);
  if (lookups.length === 0) return problems;

  for (const row of slice.rows) {
    if (row.isOrphaned !== true) continue;
    for (const column of lookups) {
      const raw: unknown = row.cells[column.code];
      const id = typeof raw === 'number' ? raw : typeof raw === 'string' && raw.trim() !== '' ? Number(raw) : Number.NaN;
      const registryId = column.lookupRegistryDefId;
      if (!Number.isInteger(id) || registryId === null) continue;
      const entry = entryOf(registryId, id);
      if (entry === undefined || coversPeriod(entry, slice.periodKey)) continue;

      problems.set(row.rowKey, {
        permit: isPermitRegistry(registryId),
        column: column.header || column.code,
        value: entry.label,
        validity: describeValidity(entry.validFrom, entry.validTo),
      });
      break;
    }
  }

  return problems;
}

function problemText(problem: RowLookupProblem, periodKey: number): string {
  const period = formatPeriodKey(periodKey) || String(periodKey);

  return problem.permit
    ? t('grid.rowAttention.permitNotCovering', { permit: problem.value, validity: problem.validity, period })
    : t('grid.rowAttention.valueNotCovering', {
        value: problem.value,
        column: problem.column,
        validity: problem.validity,
        period,
      });
}

/**
 * Причина для рядка словами; `null` — рядок уваги не потребує. Порядок дій: спершу — що виправити (дозвіл,
 * чинний у періоді), потім — що місяць закритий і його треба відкрити повторно.
 */
export function rowAttentionReason(slice: TableSliceDto, rowKey: string, problem?: RowLookupProblem): string | null {
  const row = slice.rows.find((candidate) => candidate.rowKey === rowKey);
  if (row?.isOrphaned !== true) return null;

  const locked = describeColumns(slice, lockedColumnsOf(slice, rowKey));
  const orphaned = problem === undefined ? t('grid.rowAttention.orphaned') : problemText(problem, slice.periodKey);

  return locked === null ? orphaned : `${orphaned} ${t('grid.rowAttention.locked', { months: locked })}`;
}

/**
 * ✎ F13-p210: попередження перевірки на рядку — правило (h) `PERMIT-VALIDITY`, рівень `Warning` (дозвіл не
 * покриває місяці лише з обчисленими значеннями). Береться з результату перевірки, який уже читає Issues: лише
 * знахідки цього періоду, цієї таблиці і в комірці дозволу (колонка-довідник зрізу); текст — сервера, мовою
 * читача. Кілька знахідок рядка — через пробіл. Порожньо — рядок попередження не має.
 */
export function rowWarningsOf(
  slice: TableSliceDto,
  tableDefId: number,
  findings: PeriodRowFindings | null,
): ReadonlyMap<string, string> {
  const warnings = new Map<string, string>();
  if (findings === null || findings.periodKey !== slice.periodKey) return warnings;

  const lookupColumns = new Set(slice.columns.filter((column) => column.dataType === 'Lookup').map((column) => column.code));
  const rowKeys = new Set(slice.rows.map((row) => row.rowKey));
  for (const finding of findings.findings) {
    if (finding.tableDefId !== tableDefId || !rowKeys.has(finding.rowKey)) continue;
    if (finding.columnCode === null || !lookupColumns.has(finding.columnCode)) continue;
    const before = warnings.get(finding.rowKey);
    if (before?.includes(finding.message) === true) continue;
    warnings.set(finding.rowKey, before === undefined ? finding.message : `${before} ${finding.message}`);
  }

  return warnings;
}

/** Куди веде «!» попередження: перший пункт перевірки цього рядка в Issues. */
export function rowWarningFocusKey(tableDefId: number, rowKey: string): string {
  return `finding:${String(tableDefId)}:${rowKey}`;
}

const NoProblems: ReadonlyMap<string, RowLookupProblem> = new Map();
const NoWarnings: ReadonlyMap<string, string> = new Map();
const bySlice = new WeakMap<
  TableSliceDto,
  {
    problems: ReadonlyMap<string, RowLookupProblem>;
    warnings: ReadonlyMap<string, string>;
    tableDefId: number | undefined;
    value: TableAttention;
  }
>();

/**
 * Стан таблиці для сховища; пам'ятається за об'єктом зрізу, переліком проблем і попереджень (ті самі — той
 * самий стан). Рядок з утраченим посиланням — помилка (його попередження, якщо є, дописується в причину);
 * рядок лише з попередженням — пункт `severity: 'Warning'`, що веде на знахідку перевірки.
 */
export function tableAttentionOf(
  slice: TableSliceDto,
  problems: ReadonlyMap<string, RowLookupProblem> = NoProblems,
  warnings: ReadonlyMap<string, string> = NoWarnings,
  tableDefId?: number,
): TableAttention {
  const cached = bySlice.get(slice);
  if (cached?.problems === problems && cached.warnings === warnings && cached.tableDefId === tableDefId) return cached.value;

  const rows: RowAttentionItem[] = [];
  for (const row of slice.rows) {
    const warning = warnings.get(row.rowKey);
    const reason = row.isOrphaned === true ? rowAttentionReason(slice, row.rowKey, problems.get(row.rowKey)) : null;
    if (reason === null && warning === undefined) continue;
    const base = {
      key: `${String(slice.tableInstanceId)}:${row.rowKey}`,
      tableInstanceId: slice.tableInstanceId,
      periodKey: slice.periodKey,
      rowKey: row.rowKey,
      rowLabel: row.label ?? row.rowKey,
    };
    if (reason !== null) {
      rows.push({ ...base, reason: warning === undefined ? reason : `${reason} ${warning}` });
    } else if (warning !== undefined) {
      rows.push({
        ...base,
        reason: warning,
        severity: 'Warning',
        ...(tableDefId === undefined ? {} : { focusKey: rowWarningFocusKey(tableDefId, row.rowKey) }),
      });
    }
  }

  const value: TableAttention = {
    tableInstanceId: slice.tableInstanceId,
    ...(tableDefId === undefined ? {} : { tableDefId }),
    periodKey: slice.periodKey,
    rows,
    lockedMonths: describeColumns(slice, lockedColumnsOf(slice)),
  };
  bySlice.set(slice, { problems, warnings, tableDefId, value });

  return value;
}
