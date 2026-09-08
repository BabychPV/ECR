import { apiFetch } from '@/api/client';
import type { SaveSheetDefRequest, SheetDto } from '@/api/types';
import type { SheetDraft } from './sheet';

/**
 * Звернення редактора аркушів (`ФВ-2.1`).
 *
 * ⛔ Перший вертикальний зріз авторства структури шаблону через API. До
 * цього `SheetDef`/`TableDef`/`ColumnDef` створював лише `Ecr.DataGen`
 * (генератор тестових даних), `RowDef` — лише тести: структуру шаблону не
 * можна було завести інакше, ніж написавши `INSERT` руками.
 *
 * ⚠ Аркуш адресується версією — так само, як зв'язки між таблицями (`
 * features/tables/api.ts`): правку приймає лише чернетка (`ФВ-7.1`).
 */

/**
 * Записує аркуш; створює його, якщо коду ще немає.
 *
 * ⚠ `PUT`, а не `POST`: адресою аркуша є його **код**, і задає його викликач
 * (`D2-147`), як і в зв'язку між таблицями. Створення й зміна — одна дія.
 */
export function saveSheet(templateVersionId: number, draft: SheetDraft): Promise<SheetDto> {
  return apiFetch<SheetDto>(
    `/api/v1/template-versions/${String(templateVersionId)}/sheets/${encodeURIComponent(draft.code)}`,
    {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(sheetBody(draft)),
    },
  );
}

/** Прибирає аркуш із чернетки (м'яко, `ФВ-7.6`). */
export function deleteSheet(templateVersionId: number, code: string): Promise<void> {
  return apiFetch<void>(
    `/api/v1/template-versions/${String(templateVersionId)}/sheets/${encodeURIComponent(code)}`,
    { method: 'DELETE' },
  );
}

/** Тіло запиту `PUT …/sheets/{code}`. */
function sheetBody(draft: SheetDraft): SaveSheetDefRequest {
  return {
    nameL10n: draft.nameL10n,
    ordinal: draft.ordinal,
    sheetGroup: draft.sheetGroup.trim().length === 0 ? null : draft.sheetGroup.trim(),
    isMandatory: draft.isMandatory,
    isVisible: draft.isVisible,
  };
}
