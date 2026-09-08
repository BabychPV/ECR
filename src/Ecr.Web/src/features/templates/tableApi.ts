import { apiFetch } from '@/api/client';
import type { TableDto } from '@/api/types';
import type { TableDraft } from './table';

/**
 * Звернення редактора таблиць (`W5.1`) — другий вертикальний зріз авторства
 * структури шаблону через API, той самий патерн, що й `sheetApi.ts` (W5.0).
 *
 * ⚠ Таблиця адресується ДВОМА кодами: аркуша й самої таблиці
 * (`/sheets/{sheetCode}/tables/{code}`) — код таблиці унікальний лише в межах
 * свого аркуша, так само, як на сервері (`SheetDef.AddTable`).
 */

/**
 * Тіло запиту `PUT …/tables/{code}`.
 *
 * ⛔ Оголошено тут руками, а не як псевдонім згенерованого типу (на відміну
 * від решти тіл запитів у `api/types.ts`): `SaveTableDefRequest` з'явився на
 * сервері в цьому самому зрізі (`W5.1`), а `schema.d.ts` генерується окремим
 * кроком проти живого OpenAPI і в цьому зрізі не перегенерований. Форма
 * повторює `SaveTableDefRequest` контролера поле в поле; коли схему
 * перегенерують, це оголошення можна прибрати на користь `Schemas['SaveTableDefRequest']`.
 */
export interface SaveTableDefRequest {
  nameL10n: Record<string, string>;
  ordinal: number | null;
  layoutKind: TableDto['layoutKind'];
  rowMode: TableDto['rowMode'];
  maxDynamicRows: number | null;
}

/**
 * Записує таблицю; створює її, якщо коду ще немає.
 *
 * ⚠ `PUT`, а не `POST`: адресою таблиці є її **код** у межах аркуша, і задає
 * його викликач (`D2-147`), як і аркуш. Створення й зміна — одна дія.
 */
export function saveTable(
  templateVersionId: number,
  sheetCode: string,
  draft: TableDraft,
): Promise<TableDto> {
  return apiFetch<TableDto>(
    `/api/v1/template-versions/${String(templateVersionId)}/sheets/${encodeURIComponent(sheetCode)}/tables/${encodeURIComponent(draft.code)}`,
    {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(tableBody(draft)),
    },
  );
}

/** Прибирає таблицю з аркуша чернетки (м'яко, `ФВ-7.6`). */
export function deleteTable(
  templateVersionId: number,
  sheetCode: string,
  code: string,
): Promise<void> {
  return apiFetch<void>(
    `/api/v1/template-versions/${String(templateVersionId)}/sheets/${encodeURIComponent(sheetCode)}/tables/${encodeURIComponent(code)}`,
    { method: 'DELETE' },
  );
}

/** Тіло запиту `PUT …/tables/{code}`. */
function tableBody(draft: TableDraft): SaveTableDefRequest {
  return {
    nameL10n: draft.nameL10n,
    ordinal: draft.ordinal,
    layoutKind: draft.layoutKind,
    rowMode: draft.rowMode,
    maxDynamicRows: draft.maxDynamicRows,
  };
}
