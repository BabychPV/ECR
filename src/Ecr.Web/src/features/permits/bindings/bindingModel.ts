import type { TemplateTable } from '@/features/templates/ctor/ctorModel';
import { localized } from '@/shared/i18n/localized';
import type { PermitRefreshResult } from '../api/bindingsRuntimeApi';
import type { PermitBinding, PermitPreviewWarning, SavePermitBindingRequest } from '../api/bindingsTypes';
import type { PermitTypeTable } from '../api/types';

/*
 * Модель прив'язки таблиці аркуша до таблиці типу дозволу (Permits, PM-11): чиста логіка майстра, без React.
 * Перевірки дзеркалять серверні (`PermitBindingValidator`), але ЗАВЖДИ лише підказка: останнє слово за `422`.
 */

export const BindingModes = ['Rows', 'Formulas'] as const;
export type BindingMode = (typeof BindingModes)[number];

/** Режим «рядки з дозволу» — єдиний, у якому документ має що оновлювати (DocumentTableDto.permitBinding.mode). */
export const RowsMode: BindingMode = 'Rows';

export const SelectionRules = ['ByFacilityPeriodEnd', 'FromHeaderLookup'] as const;
export type SelectionRule = (typeof SelectionRules)[number];

export const BindingRoles = ['RowKey', 'Limit', 'Info'] as const;
export type BindingRole = (typeof BindingRoles)[number];

// Ролі СТОВПЦІВ прив'язки (не ролі RBAC; серверний еквівалент - PermitColumnRole): іменовані константи, а не
// порівняння з рядком-літералом, яке сторож PrincipleTests сприймає як власний RBAC клієнта.
const RowKeyRole: BindingRole = 'RowKey';
const LimitRole: BindingRole = 'Limit';

/** Роль стовпця дозволу в зіставленні; `None` — стовпець не використовується. */
export type MappingRole = BindingRole | 'None';

/** Один стовпець таблиці дозволу в зіставленні. */
export interface MappingRow {
  readonly fieldCode: string;
  readonly role: MappingRole;
  /** Стовпець аркуша (`TemplateColumnDto.id`); `null`, доки не обрано. */
  readonly sheetColumnId: number | null;
}

/** Чернетка майстра. */
export interface BindingDraft {
  readonly permitTypeCode: string;
  readonly permitTableCode: string;
  readonly mode: BindingMode;
  readonly selectionRule: SelectionRule;
  readonly headerFieldDefId: number | null;
  readonly mapping: readonly MappingRow[];
}

/** Стовпець аркуша, як його віддає структура версії (потрібне для зіставлення). */
export interface SheetColumnRef {
  readonly id: number;
  readonly code: string;
  readonly isReadOnly: boolean;
  readonly name: string;
}

/** Таблиця аркуша, до якої прив'язують. */
export interface SheetTableRef {
  readonly id: number;
  readonly code: string;
  /** `Fixed` | `Dynamic` | `Mixed` (режим рядків структури). */
  readonly rowMode: string;
  readonly columns: readonly SheetColumnRef[];
}

/** Таблиця структури версії → довідка для зіставлення (назви стовпців — мовою користувача, інакше код). */
export function sheetRefOf(table: TemplateTable): SheetTableRef {
  return {
    id: table.id,
    code: table.code,
    rowMode: table.rowMode,
    columns: table.columns.map((column) => ({
      id: column.id,
      code: column.code,
      isReadOnly: column.isReadOnly,
      name: localized(column.headerL10n) || column.code,
    })),
  };
}

/** Коди, за якими стовпець дозволу пропонується як «Limit» (макет: `LIMIT_CODES` + слово «limit»). */
const LimitCodes: readonly string[] = ['Emission_t_year', 'Permitted_t_year', 'Annual', 'Permissible_mg_dm3'];

export const emptyDraft = (): BindingDraft => ({
  permitTypeCode: '',
  permitTableCode: '',
  mode: 'Rows',
  selectionRule: 'ByFacilityPeriodEnd',
  headerFieldDefId: null,
  mapping: [],
});

/** Код прив'язки: унікальний у системі, тож береться з `Id` таблиці версії та її коду. */
export function bindingCodeFor(table: { readonly id: number; readonly code: string }): string {
  return `PB${String(table.id)}_${table.code}`.slice(0, 64);
}

const sameCode = (a: string, b: string): boolean => a.toLowerCase() === b.toLowerCase();

/**
 * Пропонована розкладка стовпців (макет, `defaultMap`): ключі рядка → `RowKey`, відомий стовпець ліміту → `Limit`,
 * решта збігів за кодом → `Info`; без збігу за кодом стовпець лишається невикористаним.
 */
export function suggestMapping(permitTable: PermitTypeTable, sheet: SheetTableRef): MappingRow[] {
  const used = new Set<number>();

  return [...permitTable.columns]
    .sort((a, b) => a.order - b.order)
    .map((column) => {
      const match = sheet.columns.find((candidate) => !used.has(candidate.id) && candidate.isReadOnly && sameCode(candidate.code, column.code));
      if (match === undefined) return { fieldCode: column.code, role: 'None' as const, sheetColumnId: null };

      used.add(match.id);
      const role: BindingRole = column.key
        ? 'RowKey'
        : LimitCodes.some((code) => sameCode(code, column.code)) || /limit/i.test(column.code)
          ? 'Limit'
          : 'Info';

      return { fieldCode: column.code, role, sheetColumnId: match.id };
    });
}

/**
 * Розкладка для кроку «Columns»: порожня — пропозиція (`suggestMapping`); інакше (правка наявної прив'язки) —
 * наявні рядки лишаються, а стовпці дозволу, яких там не було, додаються невикористаними, у порядку таблиці.
 */
export function completeMapping(permitTable: PermitTypeTable, current: readonly MappingRow[], sheet: SheetTableRef): MappingRow[] {
  if (current.length === 0) return suggestMapping(permitTable, sheet);

  const byField = new Map(current.map((row) => [row.fieldCode, row] as const));

  return [...permitTable.columns]
    .sort((a, b) => a.order - b.order)
    .map((column) => byField.get(column.code) ?? { fieldCode: column.code, role: 'None' as const, sheetColumnId: null });
}

/** Що не так із зіставленням; `null` — гаразд. `fieldCode`/`columnCode` — для підпису. */
export type MappingProblem =
  | { readonly kind: 'noColumn'; readonly fieldCode: string }
  | { readonly kind: 'duplicateColumn'; readonly columnCode: string }
  | { readonly kind: 'notReadOnly'; readonly columnCode: string }
  | { readonly kind: 'noRowKey' }
  | { readonly kind: 'manyLimits' };

/** Перевірка зіставлення режиму `Rows` (дзеркало `bindingNoRowKey`, `bindingManyLimits`, `bindingNotReadOnly`…). */
export function mappingProblem(draft: BindingDraft, sheet: SheetTableRef): MappingProblem | null {
  if (draft.mode !== 'Rows') return null;

  const used = draft.mapping.filter((row) => row.role !== 'None');
  const seen = new Set<number>();
  for (const row of used) {
    const column = sheet.columns.find((candidate) => candidate.id === row.sheetColumnId);
    if (column === undefined) return { kind: 'noColumn', fieldCode: row.fieldCode };
    if (seen.has(column.id)) return { kind: 'duplicateColumn', columnCode: column.code };
    seen.add(column.id);
    if (!column.isReadOnly) return { kind: 'notReadOnly', columnCode: column.code };
  }

  if (!used.some((row) => row.role === RowKeyRole)) return { kind: 'noRowKey' };
  if (used.filter((row) => row.role === LimitRole).length > 1) return { kind: 'manyLimits' };

  return null;
}

/** Що не так із вибором таблиці й правила; `null` — гаразд. */
export type DraftProblem = 'typeRequired' | 'tableRequired' | 'headerFieldRequired' | 'rowsNeedDynamic';

export function draftProblem(draft: BindingDraft, sheet: SheetTableRef): DraftProblem | null {
  if (draft.permitTypeCode.length === 0) return 'typeRequired';
  if (draft.permitTableCode.length === 0) return 'tableRequired';
  if (draft.selectionRule === 'FromHeaderLookup' && draft.headerFieldDefId === null) return 'headerFieldRequired';
  // Дзеркало `bindingRowsNeedDynamic`: рядки з дозволу — лише в `Dynamic`/`Mixed`.
  if (draft.mode === 'Rows' && sheet.rowMode === 'Fixed') return 'rowsNeedDynamic';

  return null;
}

/** Тіло `PUT`: у режимі `Formulas` стовпців немає, поле шапки — лише для `FromHeaderLookup`. */
export function toRequest(draft: BindingDraft, sheet: SheetTableRef): SavePermitBindingRequest {
  const columns =
    draft.mode === 'Formulas'
      ? []
      : draft.mapping
          .filter((row): row is MappingRow & { role: BindingRole; sheetColumnId: number } => row.role !== 'None' && row.sheetColumnId !== null)
          .map((row) => ({ permitFieldCode: row.fieldCode, role: row.role, sheetColumnDefId: row.sheetColumnId }));

  return {
    permitTypeCode: draft.permitTypeCode,
    permitTableCode: draft.permitTableCode,
    sheetTableDefId: sheet.id,
    mode: draft.mode,
    selectionRule: draft.selectionRule,
    headerFieldDefId: draft.selectionRule === 'FromHeaderLookup' ? draft.headerFieldDefId : null,
    isActive: true,
    columns,
  };
}

const isRole = (value: string): value is BindingRole => (BindingRoles as readonly string[]).includes(value);

/** Чернетка з наявної прив'язки (правка). Невідома роль із сервера стає `Info`. */
export function draftOfBinding(binding: PermitBinding, permitTable: PermitTypeTable | undefined): BindingDraft {
  const byField = new Map(binding.columns.map((column) => [column.permitFieldCode, column] as const));
  const fields = permitTable === undefined ? binding.columns.map((column) => column.permitFieldCode) : [...permitTable.columns].sort((a, b) => a.order - b.order).map((column) => column.code);

  return {
    permitTypeCode: binding.permitTypeCode,
    permitTableCode: binding.permitTableCode,
    mode: binding.mode === 'Formulas' ? 'Formulas' : 'Rows',
    selectionRule: binding.selectionRule === 'FromHeaderLookup' ? 'FromHeaderLookup' : 'ByFacilityPeriodEnd',
    headerFieldDefId: binding.headerFieldDefId,
    mapping: fields.map((fieldCode) => {
      const column = byField.get(fieldCode);

      return column === undefined
        ? { fieldCode, role: 'None' as const, sheetColumnId: null }
        : { fieldCode, role: isRole(column.role) ? column.role : ('Info' as const), sheetColumnId: column.sheetColumnDefId };
    }),
  };
}

/** Причина «дозволу немає»: `reasonIfNone` сервера; невідома стає `other`. */
export type NoPermitReason = 'noFacility' | 'noPermit' | 'headerEmpty' | 'headerUnknown' | 'other';

export function reasonKind(reason: string | null): NoPermitReason {
  return reason === 'noFacility' || reason === 'noPermit' || reason === 'headerEmpty' || reason === 'headerUnknown' ? reason : 'other';
}

/** Відомі випадки попереджень перегляду й оновлення; невідомий стає `other`. */
export type WarningKind =
  | 'inactive'
  | 'formulasMode'
  | 'notRowsBinding'
  | 'documentOtherVersion'
  | 'tableUnavailable'
  | 'fieldUnreadable'
  | 'notValidAtPeriodEnd'
  | 'rowCountUnknown'
  | 'rowsClamped'
  | 'planFailed'
  | 'valueSkipped'
  | 'rowDeleted'
  | 'cellNotFromPermit'
  | 'periodUnknown'
  | 'other';

/** Випадок попередження за хвостом його коду (`binding.warning.inactive` → `inactive`). */
export function warningKind(warning: PermitPreviewWarning): WarningKind {
  const tail = warning.code.slice(warning.code.lastIndexOf('.') + 1);
  switch (tail) {
    case 'inactive':
    case 'formulasMode':
    case 'notRowsBinding':
    case 'documentOtherVersion':
    case 'tableUnavailable':
    case 'fieldUnreadable':
    case 'rowCountUnknown':
    case 'rowsClamped':
    case 'planFailed':
    case 'valueSkipped':
    case 'rowDeleted':
    case 'cellNotFromPermit':
    case 'periodUnknown':
      return tail;
    case 'permitNotValidAtPeriodEnd':
      return 'notValidAtPeriodEnd';
    case 'noPermit':
      return 'planFailed';
    default:
      return 'other';
  }
}
/** Підсумок «Оновити з дозволу» для показу: числа та ключі, без тексту. */
export interface RefreshSummary {
  readonly added: number;
  readonly updated: number;
  readonly keptKeys: readonly string[];
  readonly warnings: readonly PermitPreviewWarning[];
  /** Нічого не змінилось і нічого не лишилось «поза дозволом». */
  readonly unchanged: boolean;
}

export function refreshSummary(result: PermitRefreshResult): RefreshSummary {
  return {
    added: result.added,
    updated: result.updated,
    keptKeys: result.removedFromPermit,
    warnings: result.warnings,
    unchanged: result.added === 0 && result.updated === 0 && result.removedFromPermit.length === 0,
  };
}
