import type { components } from '@/api/schema';

/**
 * DTO скрипта методології (план `METHODOLOGY-SCRIPT-PLAN.md`, розд. 5.4) —
 * псевдоніми згенерованих схем, а не друга копія полів. Решта фічі імпортує
 * типи лише звідси.
 */
type Schemas = components['schemas'];

/** Рушій версії методології. */
export type ScriptEngine = Schemas['MethodologyEngine'];

/** Діагностика: рядок, колонка й довжина — з одиниці. */
export type ScriptDiagnosticDto = Schemas['ScriptDiagnosticDto'];

/** Таблиця, згадана в скрипті (`alias` — лише для `table Alias = …`). */
export type ScriptOutlineTableDto = Schemas['ScriptOutlineTableDto'];
export type ScriptOutlineInputDto = Schemas['ScriptOutlineInputDto'];
export type ScriptOutlineOutputDto = Schemas['ScriptOutlineOutputDto'];
export type ScriptOutlineConstantDto = Schemas['ScriptOutlineConstantDto'];
export type ScriptOutlineVariableDto = Schemas['ScriptOutlineVariableDto'];
export type ScriptOutlineFunctionDto = Schemas['ScriptOutlineFunctionDto'];
export type ScriptOutlineDto = Schemas['ScriptOutlineDto'];

/** Склад речовин версії. */
export type MethodologySubstancesDto = Schemas['MethodologySubstancesDto'];

export type MethodologyScriptDto = Schemas['MethodologyScriptDto'];
export type SaveScriptRequest = Schemas['SaveMethodologyScriptRequest'];
/** Тіло `validate` і `format-const`. */
export type ScriptTextRequest = Schemas['ScriptTextRequest'];
export type ScriptValidationDto = Schemas['ScriptValidationDto'];
export type PreviewScriptRequest = Schemas['PreviewMethodologyScriptRequest'];
export type ScriptPreviewDto = Schemas['ScriptPreviewDto'];
export type ScriptPreviewTableDto = Schemas['ScriptPreviewTable'];
export type ScriptPreviewRowDto = Schemas['ScriptPreviewRow'];
export type ScriptPreviewValueDto = Schemas['ScriptPreviewValue'];
/** `{ text | null, kind | null, diagnostics[] }`: `text = null` — літерал не розібрано. */
export type FormatConstResultDto = Schemas['FormatConstDto'];

/** Значення `ScriptPreviewRowDto.state` (малими літерами, як віддає сервер). */
export type ScriptPreviewRowState = 'selected' | 'inactive' | 'notSelected' | 'rejected';

/** Значення `ScriptPreviewRowDto.issue`. */
export type ScriptPreviewIssue = 'missingInputs' | 'stepError' | 'rejected';

/** Значення `ScriptOutlineConstantDto.kind`. */
export type ScriptConstantKind = 'scalar' | 'map';
