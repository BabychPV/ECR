/*
 * ⚠ Модуля немає ні в дереві `05-skeleton.md` §1, ні в `05i` (Q-015), але на
 * нього посилаються `features/grid/DocumentGrid.tsx` і `features/grid/useCellPatch.ts`.
 *
 * Це ДОСЛІВНИЙ переклад DTO з `02-contracts.md` §10 — нічого не додано і не
 * прибрано. Коли `npm run api:types` згенерує `schema.d.ts` з живого OpenAPI,
 * ці типи мають бути замінені на посилання в згенеровану схему, інакше вони
 * розійдуться з сервером мовчки.
 */

/** Опис колонки для клієнта. `02-contracts.md` §10, `TableSliceDto.cs`. */
export interface ColumnDto {
  id: number;
  code: string;
  header: string;
  dataType: string;
  ordinal: number;
  isReadOnly: boolean;
  isRequired: boolean;
  displayFormat: string | null;
  defaultValue: string | null;
  lookupRegistryDefId: number | null;
  unitId: number | null;
  unitSymbol: string | null;
}

/** Рядок зі значеннями; ключ у `cells` — код колонки. */
export interface RowDto {
  rowKey: string;
  ordinal: number;
  rowKind: string;
  label: string | null;
  rowVersion: string;
  /** Присутній ключ зі значенням null — явна порожнеча; відсутній ключ — «не заповнювали» (R-B4). */
  cells: Record<string, unknown>;
  /** Рядок посилається на запис реєстру, що втратив чинність (ФВ-8.13). Читання не блокує, Submit блокує. */
  isOrphaned?: boolean;
}

/**
 * Зріз таблиці для grid. Порожні комірки не передаються — клієнт бере
 * `defaultValue` з опису колонки (ФВ-3.8).
 */
export interface TableSliceDto {
  tableInstanceId: number;
  periodKey: number;
  columns: ColumnDto[];
  rows: RowDto[];
  cellPermissions: Record<string, string>;
}

/**
 * Зміна однієї комірки. Три різні операції (R-B4): значення — записати;
 * `value: null` — стерти; `isEmpty: true` — явна порожнеча;
 * поле відсутнє в запиті — не чіпати.
 */
export interface PatchCell {
  columnCode: string;
  value?: unknown;
  isEmpty?: boolean;
}

/** Рядок у пакетній зміні. `baseVersion: null` означає СТВОРЕННЯ рядка (R-B2). */
export interface PatchRow {
  rowKey: string;
  baseVersion: string | null;
  cells: PatchCell[];
}

/**
 * Пакетна зміна комірок. Часткове застосування заборонене: конфлікт у
 * будь-якому рядку відхиляє весь батч (B04 §2.3).
 */
export interface PatchCellsRequest {
  tableInstanceId: number;
  periodKey: number;
  /** `UserEdit` | `Import` | `Recalculation`. */
  origin: string;
  rows: PatchRow[];
}

/** Повідомлення валідації. */
export interface ValidationMessageDto {
  /** `Info` | `Warning` | `Error`. */
  severity: string;
  ruleCode: string;
  message: string;
  rowKey: string | null;
  columnCode: string | null;
}

/** Результат пакетної зміни. */
export interface PatchCellsResponse {
  appliedCells: number;
  /** `RowKey` → hex `rowversion`. */
  rowVersions: Record<string, string>;
  validation: ValidationMessageDto[];
}

/**
 * Конфлікт паралельного редагування. Приходить у `extensions.conflicts`
 * при `ECR-CELL-0409`. «Перезаписати мовчки» не є опцією.
 */
export interface CellConflictDto {
  rowKey: string;
  columnCode: string;
  yourValue: unknown;
  theirValue: unknown;
  theirUser: string;
  theirChangedAt: string;
  currentVersion: string;
}
