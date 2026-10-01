import type { MapColumn } from './sourceEventMapForm';
import type {
  CreateRowWindowMapRequest,
  RowWindowMap,
  RowWindowSourceInput,
  RowWindowSummary,
  UpdateRowWindowMapRequest,
} from './rowWindowApi';

/**
 * Модель форми прив'язки вікна рядка (HSE301 A1, FEATURE-HSE301-VIEW §4.4) — чиста логіка без інтерфейсу: стан
 * форми, перевірки до надсилання і збирання тіла `POST`/`PUT`.
 *
 * ⛔ Перевірки тут — підказка ДО надсилання, а не заміна серверних: сервер лишається остаточним
 * (`err.ECR-INT-0422.windowColumnsNotDate`, `.targetNotDecimal`, `.rowWindowPolicyOutOfRange` …), форма лише не дає
 * натиснути «Зберегти», коли відмова відома наперед.
 */

export const SummaryKinds = ['Total', 'Average', 'Minimum', 'Maximum', 'Count'] as const satisfies readonly RowWindowSummary[];

/** Верхня межа діб повтору — як `RowWindowMap.MaxRefetchWithinDays`. */
const MaxRefetchDays = 366;

/** Рядок переліку джерел. `key` — лише для React, на сервер не йде. */
export interface SourceRow {
  readonly key: string;
  readonly selectorValue: string;
  readonly sourceEntityId: number | null;
  readonly sourceField: string;
  readonly sourceUnitId: number | null;
}

export interface RowWindowFormState {
  /** Документ потрібен лише щоб прочитати колонки таблиці; на сервер не йде. */
  readonly documentId: number | null;
  readonly tableDefId: number | null;
  readonly targetColumnDefId: number | null;
  readonly startColumnDefId: number | null;
  readonly endColumnDefId: number | null;
  readonly selectorColumnDefId: number | null;
  readonly summary: RowWindowSummary;
  readonly isStep: boolean;
  readonly targetUnitId: number | null;
  /** Порожньо — типове значення сервера (95). */
  readonly minPercentGood: string;
  /** Порожньо — типове значення сервера (7). */
  readonly refetchWithinDays: string;
  /** Порожньо — порога немає. */
  readonly maxGapSeconds: string;
  readonly isActive: boolean;
  readonly sources: readonly SourceRow[];
}

export type RowWindowProblem =
  | 'documentRequired'
  | 'tableRequired'
  | 'columnsRequired'
  | 'windowNotDate'
  | 'targetNotDecimal'
  | 'windowSame'
  | 'unitRequired'
  | 'policyInvalid'
  | 'sourceIncomplete'
  | 'selectorWithoutColumn'
  | 'duplicateSelector';

let rowSeed = 0;

/** Новий порожній рядок джерела. */
export function newSourceRow(patch: Partial<Omit<SourceRow, 'key'>> = {}): SourceRow {
  rowSeed += 1;

  return { key: `s${String(rowSeed)}`, selectorValue: '', sourceEntityId: null, sourceField: '', sourceUnitId: null, ...patch };
}

export function emptyForm(): RowWindowFormState {
  return {
    documentId: null,
    tableDefId: null,
    targetColumnDefId: null,
    startColumnDefId: null,
    endColumnDefId: null,
    selectorColumnDefId: null,
    summary: 'Total',
    isStep: false,
    targetUnitId: null,
    minPercentGood: '',
    refetchWithinDays: '',
    maxGapSeconds: '',
    isActive: true,
    sources: [],
  };
}

/** Форма наявної прив'язки; документ обирають знову — прив'язка його не зберігає. */
export function formFromMap(map: RowWindowMap): RowWindowFormState {
  return {
    documentId: null,
    tableDefId: map.tableDefId,
    targetColumnDefId: map.targetColumnDefId,
    startColumnDefId: map.startColumnDefId,
    endColumnDefId: map.endColumnDefId,
    selectorColumnDefId: map.selectorColumnDefId,
    summary: map.summary,
    isStep: map.isStep,
    targetUnitId: map.targetUnitId,
    minPercentGood: map.minPercentGood,
    refetchWithinDays: String(map.refetchWithinDays),
    maxGapSeconds: map.maxGapSeconds === null ? '' : String(map.maxGapSeconds),
    isActive: map.isActive,
    sources: map.sources.map((source) =>
      newSourceRow({
        selectorValue: source.selectorValue ?? '',
        sourceEntityId: source.sourceEntityId,
        sourceField: source.sourceField,
        sourceUnitId: source.sourceUnitId,
      }),
    ),
  };
}

function isWhole(text: string, max: number): boolean {
  return /^\d+$/.test(text) && Number(text) <= max;
}

function policyValid(state: RowWindowFormState): boolean {
  const percent = state.minPercentGood.trim();
  const days = state.refetchWithinDays.trim();
  const gap = state.maxGapSeconds.trim();

  const percentOk = percent.length === 0 || (/^\d+(\.\d{1,2})?$/.test(percent) && Number(percent) <= 100);
  const daysOk = days.length === 0 || isWhole(days, MaxRefetchDays);
  const gapOk = gap.length === 0 || (isWhole(gap, Number.MAX_SAFE_INTEGER) && Number(gap) > 0 && Number(gap) <= 2147483647);

  return percentOk && daysOk && gapOk;
}

/** Що не дає зберегти форму; порожньо — можна. */
export function validateForm(state: RowWindowFormState, columns: readonly MapColumn[]): RowWindowProblem[] {
  const problems: RowWindowProblem[] = [];
  const byId = new Map(columns.map((column) => [column.id, column]));
  const type = (id: number | null): string | undefined => (id === null ? undefined : byId.get(id)?.dataType);

  if (state.documentId === null) problems.push('documentRequired');
  if (state.tableDefId === null) problems.push('tableRequired');

  if (state.targetColumnDefId === null || state.startColumnDefId === null || state.endColumnDefId === null) {
    problems.push('columnsRequired');
  }

  const start = type(state.startColumnDefId);
  const end = type(state.endColumnDefId);
  if ((start !== undefined && start !== 'Date') || (end !== undefined && end !== 'Date')) problems.push('windowNotDate');

  const target = type(state.targetColumnDefId);
  if (target !== undefined && target !== 'Decimal') problems.push('targetNotDecimal');

  if (state.startColumnDefId !== null && state.startColumnDefId === state.endColumnDefId) problems.push('windowSame');
  if (state.targetUnitId === null) problems.push('unitRequired');
  if (!policyValid(state)) problems.push('policyInvalid');

  if (state.sources.some((source) => source.sourceEntityId === null || source.sourceUnitId === null || source.sourceField.trim().length === 0)) {
    problems.push('sourceIncomplete');
  }

  const selectors = state.sources.map((source) => source.selectorValue.trim().toLowerCase());
  if (state.selectorColumnDefId === null && selectors.some((value) => value.length > 0)) {
    problems.push('selectorWithoutColumn');
  }

  if (new Set(selectors).size !== selectors.length) problems.push('duplicateSelector');

  return problems;
}

function need(value: number | null): number {
  if (value === null) throw new Error('The row-window form is assembled only after validation.');

  return value;
}

function sourcesOf(state: RowWindowFormState): RowWindowSourceInput[] {
  return state.sources.map((source) => ({
    selectorValue: source.selectorValue.trim().length === 0 ? null : source.selectorValue.trim(),
    sourceEntityId: need(source.sourceEntityId),
    sourceField: source.sourceField.trim(),
    sourceUnitId: need(source.sourceUnitId),
  }));
}

function policyOf(state: RowWindowFormState): {
  minPercentGood: string | null;
  refetchWithinDays: number | null;
  maxGapSeconds: number | null;
} {
  const percent = state.minPercentGood.trim();
  const days = state.refetchWithinDays.trim();
  const gap = state.maxGapSeconds.trim();

  return {
    minPercentGood: percent.length === 0 ? null : percent,
    refetchWithinDays: days.length === 0 ? null : Number(days),
    maxGapSeconds: gap.length === 0 ? null : Number(gap),
  };
}

/** Тіло `POST`; викликати лише коли `validateForm` порожній. */
export function toCreateRequest(state: RowWindowFormState): CreateRowWindowMapRequest {
  return {
    tableDefId: need(state.tableDefId),
    targetColumnDefId: need(state.targetColumnDefId),
    startColumnDefId: need(state.startColumnDefId),
    endColumnDefId: need(state.endColumnDefId),
    selectorColumnDefId: state.selectorColumnDefId,
    summary: state.summary,
    isStep: state.isStep,
    targetUnitId: need(state.targetUnitId),
    ...policyOf(state),
    sources: sourcesOf(state),
  };
}

/** Тіло `PUT` (повна заміна); `rowVersion` — версія, яку бачив клієнт. */
export function toUpdateRequest(state: RowWindowFormState, map: RowWindowMap): UpdateRowWindowMapRequest {
  return {
    startColumnDefId: need(state.startColumnDefId),
    endColumnDefId: need(state.endColumnDefId),
    selectorColumnDefId: state.selectorColumnDefId,
    summary: state.summary,
    isStep: state.isStep,
    targetUnitId: need(state.targetUnitId),
    ...policyOf(state),
    isActive: state.isActive,
    rowVersion: map.rowVersion,
    sources: sourcesOf(state),
  };
}

/** Пауза чи відновлення: усе, як є, лише `isActive` навпаки. */
export function toggleActiveRequest(map: RowWindowMap): UpdateRowWindowMapRequest {
  return {
    startColumnDefId: map.startColumnDefId,
    endColumnDefId: map.endColumnDefId,
    selectorColumnDefId: map.selectorColumnDefId,
    summary: map.summary,
    isStep: map.isStep,
    targetUnitId: map.targetUnitId,
    minPercentGood: map.minPercentGood,
    refetchWithinDays: map.refetchWithinDays,
    maxGapSeconds: map.maxGapSeconds,
    isActive: !map.isActive,
    rowVersion: map.rowVersion,
    sources: map.sources.map((source) => ({
      selectorValue: source.selectorValue,
      sourceEntityId: source.sourceEntityId,
      sourceField: source.sourceField,
      sourceUnitId: source.sourceUnitId,
    })),
  };
}
