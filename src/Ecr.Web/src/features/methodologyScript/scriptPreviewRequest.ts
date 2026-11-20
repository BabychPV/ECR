import type {
  PreviewScriptRequest,
  ScriptOutlineDto,
  ScriptPreviewRowDto,
  ScriptPreviewTableDto,
} from './types';

/** Стеля рядків перегляду на сервері. */
export const MaxPreviewRows = 200;
export const DefaultPreviewRows = 50;

export interface PreviewSelection {
  readonly documentId: number | null;
  readonly periodKey: number | null;
  /** `null` — усі екземпляри таблиць документа. */
  readonly tableInstanceId: number | null;
  readonly maxRows?: number;
}

/**
 * Запит перегляду. ⛔ Текст — ПОТОЧНИЙ текст редактора (зі збереженим чи без),
 * бо людина дивиться на те, що щойно написала; порожній текст не шлемо — тоді
 * сервер візьме збережений.
 *
 * `null` — перегляд ще не можна просити: не обрано документ або період.
 */
export function buildPreviewRequest(
  text: string,
  selection: PreviewSelection,
): PreviewScriptRequest | null {
  if (selection.documentId === null || selection.periodKey === null) return null;

  const maxRows = Math.min(
    MaxPreviewRows,
    Math.max(1, Math.trunc(selection.maxRows ?? DefaultPreviewRows)),
  );

  return {
    documentId: selection.documentId,
    periodKey: selection.periodKey,
    maxRows,
    text: text.trim() === '' ? null : text,
    tableInstanceId: selection.tableInstanceId,
  };
}

/** Екземпляр таблиці, до якого належить рядок перегляду. */
export function tableOfRow(
  tables: readonly ScriptPreviewTableDto[],
  row: ScriptPreviewRowDto,
): ScriptPreviewTableDto | undefined {
  return tables.find((table) => table.rows.includes(row));
}

/** Єдиний шаблон, названий у скрипті; `null` — не названо або їх кілька (тоді документи не звужуються). */
export function singleTemplateCode(outline: ScriptOutlineDto | null): string | null {
  const codes = new Set<string>();
  for (const table of outline?.tables ?? []) {
    if (table.templateCode !== null && table.templateCode !== '') codes.add(table.templateCode);
  }
  return codes.size === 1 ? ([...codes][0] ?? null) : null;
}

/** Період за замовчуванням для документа: перший відкритий, інакше перший; `null` — станів немає. */
export function defaultPeriodKey(
  periodStates: readonly { readonly periodKey: number; readonly state: string }[] | null | undefined,
): number | null {
  if (periodStates === undefined || periodStates === null || periodStates.length === 0) return null;

  return (periodStates.find((item) => item.state === 'Open') ?? periodStates[0])?.periodKey ?? null;
}
