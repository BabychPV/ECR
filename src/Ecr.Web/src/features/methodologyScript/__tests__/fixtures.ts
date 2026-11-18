import type {
  ScriptDiagnosticDto,
  ScriptOutlineDto,
  ScriptOutlineTableDto,
  ScriptPreviewRowDto,
  ScriptPreviewTableDto,
  ScriptPreviewValueDto,
} from '../types';

/** Побудовники DTO для тестів: сервер віддає всі поля, тож тест не повинен їх пропускати. */

export function diagnostic(
  patch: Partial<ScriptDiagnosticDto> & Pick<ScriptDiagnosticDto, 'code'>,
): ScriptDiagnosticDto {
  return {
    messageKey: '',
    params: {},
    line: 1,
    column: 1,
    length: 1,
    message: '',
    ...patch,
  };
}

export function outlineTable(code: string, alias: string | null = null, templateCode: string | null = null): ScriptOutlineTableDto {
  return { code, alias, templateCode };
}

export function outline(patch: Partial<ScriptOutlineDto> = {}): ScriptOutlineDto {
  return {
    tables: [],
    inputs: [],
    outputs: [],
    substances: [],
    imports: [],
    constants: [],
    functions: [],
    variables: [],
    ...patch,
  };
}

export function previewValue(
  patch: Partial<ScriptPreviewValueDto> & Pick<ScriptPreviewValueDto, 'name' | 'text'>,
): ScriptPreviewValueDto {
  return { kind: 'output', substance: null, error: null, ...patch };
}

export function previewRow(
  patch: Partial<ScriptPreviewRowDto> & Pick<ScriptPreviewRowDto, 'rowKey' | 'rowNumber'>,
): ScriptPreviewRowDto {
  return { state: 'selected', issue: null, missing: null, values: [], ...patch };
}

export function previewTable(
  patch: Partial<ScriptPreviewTableDto> & Pick<ScriptPreviewTableDto, 'tableCode'>,
): ScriptPreviewTableDto {
  return {
    tableInstanceId: 1,
    totalRows: 0,
    selectedRows: 0,
    inactiveRows: 0,
    rows: [],
    ...patch,
  };
}
