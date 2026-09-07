import { apiFetch } from '@/api/client';
import type { TableRelationDto, TableRelationsDto } from '@/api/types';
import { relationBody, type RelationDraft } from './relation';

/**
 * Звернення редактора зв'язків між таблицями (`ФВ-2.13`).
 *
 * ⛔ Зв'язки налаштовуються **у вебі**, а не в конфігах чи коді — і саме тому
 * цей модуль існує. Доти механізм `TableRelationDef` мав таблицю в базі,
 * сутність у домені й жодного шляху, яким людина могла б завести зв'язок: він
 * з'явився б у системі лише через `INSERT` руками, тобто рівно так, як
 * `ФВ-2.13` забороняє.
 *
 * ⚠ Зв'язки читаються з версії, а не з шаблону: вони посилаються на
 * `TableDef`, а таблиці належать версії. Версія в адресі задає ще й межу
 * правки — приймає її лише чернетка (`ФВ-7.1`).
 */

/**
 * Зв'язки версії разом зі станом версії.
 *
 * ⛔ Конверт, а не масив: порожній перелік — норма (механізм опційний), і саме
 * на ньому масив нічого не сказав би про те, чи можна правити. Клієнт показав
 * би кнопку «новий зв'язок» на опублікованій версії, де сервер однаково
 * відмовить.
 */
export function tableRelations(templateVersionId: number): Promise<TableRelationsDto> {
  return apiFetch<TableRelationsDto>(
    `/api/v1/template-versions/${String(templateVersionId)}/relations`,
  );
}

/**
 * Записує зв'язок; створює його, якщо коду ще немає.
 *
 * ⚠ `PUT`, а не `POST`: адресою зв'язку є його **код**, і задає його викликач
 * (`D2-147`). Тому створення й зміна — одна дія, і повторний запит із тим
 * самим тілом дає той самий стан.
 *
 * ⚠ Код іде через `encodeURIComponent`: у ньому дозволені лише латиниця,
 * цифри й підкреслення (`EcrCode`), але покладатися на це в побудові адреси
 * означало б, що перша ж послаблена перевірка коду ламає маршрутизацію мовчки.
 */
export function saveTableRelation(
  templateVersionId: number,
  draft: RelationDraft,
): Promise<TableRelationDto> {
  return apiFetch<TableRelationDto>(
    `/api/v1/template-versions/${String(templateVersionId)}/relations/${encodeURIComponent(draft.code)}`,
    {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(relationBody(draft)),
    },
  );
}

/** Прибирає зв'язок із чернетки. */
export function deleteTableRelation(templateVersionId: number, code: string): Promise<void> {
  return apiFetch<void>(
    `/api/v1/template-versions/${String(templateVersionId)}/relations/${encodeURIComponent(code)}`,
    { method: 'DELETE' },
  );
}
