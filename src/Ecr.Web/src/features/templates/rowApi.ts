import { apiFetch } from '@/api/client';
import { rowBody, type RowDefDto, type RowDraft } from './row';

/**
 * Звернення редактора рядків (`W5.2`) — за зразком `sheetApi.ts` (`ФВ-2.1`,
 * `W5.0`) і `columnApi.ts`.
 *
 * ⚠ Таблиця адресується числовим `tableId` — рядок і колонка сиблінги під
 * тією самою таблицею, тому адресуються однаково (див. коментар класу в
 * `Ecr.Application.Templates.SaveRowDefHandler`).
 */

/**
 * Записує рядок; створює його, якщо ключа ще немає.
 *
 * ⚠ `PUT`, а не `POST` — та сама причина, що й у `saveSheet`/`saveColumn`
 * (`D2-147`).
 */
export function saveRow(
  templateVersionId: number,
  tableId: number,
  draft: RowDraft,
): Promise<RowDefDto> {
  return apiFetch<RowDefDto>(
    `/api/v1/template-versions/${String(templateVersionId)}/tables/${String(tableId)}/rows/${encodeURIComponent(draft.rowKey)}`,
    {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(rowBody(draft)),
    },
  );
}

/** Прибирає рядок із чернетки (м'яко, `ФВ-7.6`). */
export function deleteRow(templateVersionId: number, tableId: number, rowKey: string): Promise<void> {
  return apiFetch<void>(
    `/api/v1/template-versions/${String(templateVersionId)}/tables/${String(tableId)}/rows/${encodeURIComponent(rowKey)}`,
    { method: 'DELETE' },
  );
}
