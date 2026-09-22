import { apiFetch } from '@/api/client';
import type { UsageResponse } from '@/features/registries/api';
import { columnBody, type ColumnDefDto, type ColumnDraft } from './column';
import { styleBody, type StyleDefDto } from './style';

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
 *
 * ⛔ Директива registry-lookup / cell-style, PR B1: коли форма несе чернетку
 * стилю (`draft.style !== null`), стиль записується ПЕРШИМ окремим `PUT
 * …/styles/{code}` — колонці потрібен щойно присвоєний `StyleDef.Id`, якого
 * до цього виклику просто не існує. Дві мережеві дії, а не одна, — той
 * самий компроміс, що вже прийнятий для `ColumnEditor.tsx` A3 (реєстр
 * обирається окремим запитом, колонка зберігається окремим): стиль і
 * колонка — різні сутності з різними CRUD-ендпоінтами (`B.0` директиви), і
 * об'єднувати їх в один запит означало б вигадати контракт, якого сервер не
 * оголошує.
 */
export async function saveColumn(
  templateVersionId: number,
  tableId: number,
  draft: ColumnDraft,
): Promise<ColumnDefDto> {
  const styleId =
    draft.style === null
      ? draft.styleId
      : (
          await apiFetch<StyleDefDto>(
            `/api/v1/template-versions/${String(templateVersionId)}/styles/${encodeURIComponent(draft.style.code)}`,
            {
              method: 'PUT',
              headers: { 'Content-Type': 'application/json' },
              body: JSON.stringify(styleBody(draft.style)),
            },
          )
        ).id;

  return apiFetch<ColumnDefDto>(
    `/api/v1/template-versions/${String(templateVersionId)}/tables/${String(tableId)}/columns/${encodeURIComponent(draft.code)}`,
    {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ ...columnBody(draft), styleId }),
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

/**
 * Де використовується колонка (ФВ-8.14): формули шаблону й прив'язки
 * методологій, що на неї посилаються. Право `Template.View`.
 *
 * ⚠ Адресується числовим `ColumnDefDto.id`, а не парою `tableId`/`code` —
 * саме так її адресує сервер (`GET /api/v1/column-defs/{id}/usage`).
 */
export function columnUsage(columnDefId: number): Promise<UsageResponse> {
  return apiFetch<UsageResponse>(`/api/v1/column-defs/${String(columnDefId)}/usage`);
}
