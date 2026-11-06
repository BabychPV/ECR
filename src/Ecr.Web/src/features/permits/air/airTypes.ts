import type { components } from '@/api/schema';

type Schemas = components['schemas'];

/** Картка дозволу «Air Limits Import»: джерела (НАЕ) з речовинами й підсумки (`GET /permits/{id}/air-limits`). */
export type AirLimitsPermit = Schemas['AirLimitsPermitDto'];
export type AirSource = Schemas['AirSourceDto'];
export type AirSubstance = Schemas['AirSubstanceDto'];
export type AirTotal = Schemas['AirTotalDto'];
/** P1 07.11: колонка, додана до таблиці AIR_SOURCES / AIR_SOURCE_SUBSTANCES («Add as new column»). */
export type AirAddedColumn = Schemas['AirAddedColumnDto'];

/** Профілі імпорту та їх специфікація (`RegistryImportSpec`). */
export type ImportProfile = Schemas['ImportProfileDto'];
export type ImportProfileSummary = Schemas['ImportProfileSummaryDto'];
export type ImportSpec = Schemas['RegistryImportSpec'];
export type ImportFieldSpec = Schemas['ImportFieldSpec'];

/** Відповіді майстра. */
export type AirAnalysis = Schemas['AirLimitsAnalysisDto'];
export type AirPreview = Schemas['AirLimitsPreviewDto'];
export type AirImportResult = Schemas['AirLimitsImportResultDto'];
export type ImportIssue = Schemas['ImportIssue'];
export type MissingReference = Schemas['MissingReference'];
export type OwnerReplace = Schemas['OwnerReplace'];
export type ImportHeaderReport = Schemas['ImportHeaderReport'];
export type MappedColumn = Schemas['MappedColumn'];
export type UnmappedColumn = Schemas['UnmappedColumn'];

/** Рішення про код НАЕ, якого немає в дереві (частина `request` у multipart). */
export interface MissingResolutionRequest {
  readonly kind: string;
  readonly code: string;
  readonly action: 'skip' | 'create';
  /** Id вузла дерева НАЕ, під яким створити (рядок, як у контракті). */
  readonly createUnder?: string;
}

/**
 * JSON-частина `request` викликів `analyse`/`preview`/`apply` (`AirLimitsImportOptions` на сервері; у схемі її
 * немає — це рядок форми, тож тип описано тут).
 */
export interface AirImportOptions {
  readonly profileCode?: string;
  readonly spec?: ImportSpec;
  readonly resolutions?: readonly MissingResolutionRequest[];
  /** `ownerKey` (id дозволу текстом) дозволів, чиї рядки людина погодилась замінити; лише `apply`. */
  readonly confirmedOtherOwners?: readonly string[];
  readonly expectedFingerprint?: string;
}

/** Тіло `PUT /air-limits/import-profiles/{code}`. */
export interface SaveImportProfileRequest {
  readonly nameL10n: Readonly<Record<string, string>>;
  readonly spec: ImportSpec;
  readonly rowVersion: string | null;
}

/** Вузол плоского дерева НАЕ (`GET /registries/NAE_TREE/tree`). */
export type AirScopeCodes = Schemas['AirScopeCodesDto'];
export type AirRegionCode = Schemas['AirRegionCodeDto'];
/** AIR-ON-AIR: REGION / AREA scope columns as configured on the AIR type (`GET /air-limits/scope-fields`). */
export type AirScopeFields = Schemas['AirScopeFieldsDto'];
export type AirScopeField = Schemas['AirScopeFieldDto'];
export type AirScopeEntry = Schemas['AirScopeEntryDto'];

export type NaeTreeNode = Schemas['RegistryTreeNodeDto'];
export type NaeTree = Schemas['RegistryTreeDto'];
