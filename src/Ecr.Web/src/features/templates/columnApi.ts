import { apiFetch } from '@/api/client';
import { columnBody, type ColumnDefDto, type ColumnDraft } from './column';

/**
 * Звернення редактора колонок (`W5.2`) — за зразком `sheetApi.ts` (`ФВ-2.1`,
 * `W5.0`).
 *
 * ⚠ Таблиця адресується числовим `tableId`, а не парою кодів аркуша й
 * таблиці: та сама форма, що й `SaveTableRelationRequest.SourceTableDefId`/
 * `TargetTableDefId` (`features/tables/api.ts`) — клієнт уже має
 * `TableDto.id` з попереднього `GET …/structure`.
 */

/**
 * Записує колонку; створює її, якщо коду ще немає.
 *
 * ⚠ `PUT`, а не `POST` — та сама причина, що й у `saveSheet` (`D2-147`).
 */
export function saveColumn(
  templateVersionId: number,
  tableId: number,
  draft: ColumnDraft,
): Promise<ColumnDefDto> {
  return apiFetch<ColumnDefDto>(
    `/api/v1/template-versions/${String(templateVersionId)}/tables/${String(tableId)}/columns/${encodeURIComponent(draft.code)}`,
    {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(columnBody(draft)),
    },
  );
}

/** Прибирає колонку з чернетки (м'яко, `ФВ-7.6`). */
export function deleteColumn(templateVersionId: number, tableId: number, code: string): Promise<void> {
  return apiFetch<void>(
    `/api/v1/template-versions/${String(templateVersionId)}/tables/${String(tableId)}/columns/${encodeURIComponent(code)}`,
    { method: 'DELETE' },
  );
}
