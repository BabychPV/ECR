import type { DocumentTableDto, SheetDto } from '@/api/types';
import { localized, type LocalizedText } from '@/shared/i18n/localized';

/*
 * TODO(contract): replace with schema.d.ts types.
 *
 * Сервер (lane/integ-repeatable-sheets-m) ще не запушив контракт, тому ПЛАНОВА форма
 * лежить тут, в одному файлі. Коли `openapi.snapshot.json` / `schema.d.ts` з'являться, ці
 * типи замінюються на згенеровані (`DocumentTableDto['sheetCopyId']` тощо), а `copyFieldsOf`
 * і `isSheetCopyable` стають звичайним доступом до полів.
 *
 * Планова форма:
 *   SheetDto.isCopyable: boolean
 *   DocumentTableDto.sheetCopyId?: number | null   (null / немає — оригінал)
 *   DocumentTableDto.copyOrdinal: number           (1 — оригінал)
 *   DocumentTableDto.displayNameL10n: LocalizedText (назва групи аркушів, яку можна перейменувати)
 *   DocumentTableDto.isCopyable: boolean
 */

/** Планові поля копій аркуша в таблиці документа. Необов'язкові, доки контракту немає. */
export interface PlannedTableCopyFields {
  readonly sheetCopyId?: number | null;
  readonly copyOrdinal?: number;
  readonly displayNameL10n?: LocalizedText;
  readonly isCopyable?: boolean;
}

/** Рядок перелічення копій аркуша після застосування значень за замовчуванням. */
export interface TableCopyFields {
  /** `null` — оригінал. */
  readonly sheetCopyId: number | null;
  /** 1 — оригінал; копії нумеруються далі. */
  readonly copyOrdinal: number;
  readonly displayNameL10n: LocalizedText | undefined;
  readonly isCopyable: boolean;
}

/** Поля копій таблиці; старий сервер (без полів) дає «оригінал, не копіюється». */
export function copyFieldsOf(table: DocumentTableDto): TableCopyFields {
  const planned = table as DocumentTableDto & PlannedTableCopyFields;

  return {
    sheetCopyId: planned.sheetCopyId ?? null,
    copyOrdinal: planned.copyOrdinal ?? 1,
    displayNameL10n: planned.displayNameL10n,
    isCopyable: planned.isCopyable ?? false,
  };
}

/** `SheetDto.isCopyable` шаблону (планове поле). */
export function isSheetCopyable(sheet: SheetDto): boolean {
  return (sheet as SheetDto & { isCopyable?: boolean }).isCopyable ?? false;
}

/** Ключ вкладки: оригінал — код аркуша (адреса `?sheet=` не змінилась), копія — `код~id`. */
export function sheetTabKey(sheetCode: string, sheetCopyId: number | null): string {
  return sheetCopyId === null ? sheetCode : `${sheetCode}~${String(sheetCopyId)}`;
}

/**
 * Підпис вкладки.
 *
 * Рішення: копія — «<Назва> N» (N = `copyOrdinal`); оригінал — «<Назва>», а коли у групи є копії, —
 * «<Назва> 1», щоб вкладки групи читалися як ряд 1, 2, 3 і оригінал не відрізнявся від копій лише
 * відсутністю числа.
 */
export function sheetTabLabel(name: string, copyOrdinal: number, isCopy: boolean, groupHasCopies: boolean): string {
  return isCopy || groupHasCopies ? `${name} ${String(copyOrdinal)}` : name;
}

/** Назва групи аркушів: перейменована (`displayNameL10n`), інакше назва шаблону. */
export function groupNameOf(table: DocumentTableDto): string {
  return localized(copyFieldsOf(table).displayNameL10n) || localized(table.sheetNameL10n) || '—';
}

/** Відповідь на `POST …/copies` — нова таблиця (планова). */
export type CreatedSheetCopy = DocumentTableDto & PlannedTableCopyFields;

/**
 * Тіло `PUT …/sheets/{sheetDefId}/name`.
 *
 * Припущення: та сама форма, що й у `SaveSheetDefRequest.nameL10n` — мова → текст.
 */
export interface RenameSheetGroupRequest {
  readonly nameL10n: Record<string, string>;
}
