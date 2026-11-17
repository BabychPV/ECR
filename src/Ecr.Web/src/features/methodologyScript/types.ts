/* eslint-disable no-restricted-syntax -- D-137: ендпоїнтів ще немає в schema.d.ts; після `npm run api:types` файл стає псевдонімами `Schemas[...]`. */
/**
 * DTO скрипта методології (план `METHODOLOGY-SCRIPT-PLAN.md`, розд. 5.4).
 *
 * ⚠ Типи написані вручну, бо ендпоїнтів ще немає в `openapi.snapshot.json`.
 * Після `npm run api:types` цей файл замінюється псевдонімами згенерованих
 * схем — решта фічі імпортує типи лише звідси.
 */

/** Рушій версії методології. */
export type ScriptEngine = 'Formulas' | 'Script';

/** Параметри локалізованого повідомлення. */
export type ScriptMessageParams = Readonly<Record<string, string | number>>;

/** Діагностика: рядок, колонка й довжина — з одиниці. */
export interface ScriptDiagnosticDto {
  readonly code: string;
  readonly messageKey: string | null;
  readonly params: ScriptMessageParams | null;
  readonly line: number;
  readonly column: number;
  readonly length: number;
  /** Англійський запасний текст сервера. */
  readonly message: string;
}

/**
 * Таблиця, згадана в скрипті.
 *
 * ⚠ Форму елемента `outline.tables` план не уточнює; припущено об'єкт.
 * `alias` заповнений для `table Alias = …;`, для `rows from …` — порожній.
 */
export interface ScriptOutlineTableDto {
  readonly code: string;
  readonly alias?: string | null;
  readonly templateCode?: string | null;
}

export interface ScriptOutlineInputDto {
  readonly name: string;
  readonly optional: boolean;
}

export interface ScriptOutlineOutputDto {
  readonly code: string;
  readonly unit: string;
  readonly perSubstance: boolean;
}

export type ScriptConstantKind = 'scalar' | 'map';

export interface ScriptOutlineConstantDto {
  readonly name: string;
  readonly kind: ScriptConstantKind;
  readonly line: number;
}

export interface ScriptOutlineDto {
  readonly tables: readonly ScriptOutlineTableDto[];
  readonly inputs: readonly ScriptOutlineInputDto[];
  readonly outputs: readonly ScriptOutlineOutputDto[];
  readonly substances: readonly string[];
  readonly imports: readonly string[];
  readonly constants: readonly ScriptOutlineConstantDto[];
}

export interface MethodologyScriptDto {
  readonly engine: ScriptEngine;
  readonly text: string;
  /** Хеш тексту (hex) — для `expectedHash`. */
  readonly hash: string;
  readonly isDraft: boolean;
  readonly diagnostics: readonly ScriptDiagnosticDto[];
  readonly outline: ScriptOutlineDto;
}

export interface SaveScriptRequest {
  readonly text: string;
  readonly expectedHash?: string | undefined;
}

export interface ValidateScriptRequest {
  readonly text: string;
}

export interface ScriptValidationDto {
  readonly diagnostics: readonly ScriptDiagnosticDto[];
  readonly outline: ScriptOutlineDto;
}

export interface PreviewScriptRequest {
  /** Порожній — збережений текст версії. */
  readonly text?: string | undefined;
  readonly documentId: number;
  readonly periodKey: number;
  readonly tableInstanceId?: number | undefined;
  /** До 200, типово 50. */
  readonly maxRows?: number | undefined;
}

export type ScriptPreviewRowState = 'selected' | 'inactive' | 'notSelected' | 'rejected';

export type ScriptPreviewIssue = 'missingInputs' | 'stepError' | 'rejected';

export interface ScriptPreviewValueDto {
  readonly name: string;
  readonly substance?: string | null;
  readonly kind: string;
  readonly text: string;
  readonly error?: string | null;
}

export interface ScriptPreviewRowDto {
  readonly rowKey: string;
  readonly rowNumber: number;
  readonly state: ScriptPreviewRowState;
  readonly issue?: ScriptPreviewIssue | null;
  readonly missing?: readonly string[] | null;
  /** Порожній для `notSelected` / `inactive`. */
  readonly values: readonly ScriptPreviewValueDto[];
}

export interface ScriptPreviewTableDto {
  readonly tableInstanceId: number;
  readonly tableCode: string;
  readonly totalRows: number;
  readonly selectedRows: number;
  readonly inactiveRows: number;
  readonly rows: readonly ScriptPreviewRowDto[];
}

export interface ScriptPreviewDto {
  readonly tables: readonly ScriptPreviewTableDto[];
  readonly diagnostics: readonly ScriptDiagnosticDto[];
}

export interface FormatConstRequest {
  readonly text: string;
}

export interface FormatConstResponse {
  readonly text: string;
}
