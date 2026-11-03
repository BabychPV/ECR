import type { DocumentTableDto, RenameSheetGroupRequest, SheetCopyCreated } from '@/api/types';
import { localized } from '@/shared/i18n/localized';

/*
 * Типи копій аркушів — згенеровані (`schema.d.ts`): `DocumentTableDto.sheetCopyId` / `copyOrdinal` /
 * `isCopyable` / `displayNameL10n`, `SheetDto.isCopyable`, `SheetCopyCreated`, `RenameSheetGroupRequest`.
 */

export type { RenameSheetGroupRequest, SheetCopyCreated };

/** Поля копій таблиці з нормалізованим `sheetCopyId` (`undefined` / `null` — оригінал). */
export interface TableCopyFields {
  /** `null` — оригінал. */
  readonly sheetCopyId: number | null;
  /** 1 — оригінал; копії нумеруються далі. */
  readonly copyOrdinal: number;
  readonly displayNameL10n: DocumentTableDto['displayNameL10n'];
  readonly isCopyable: boolean;
}

/** Поля копій таблиці; `sheetCopyId` необов'язковий у контракті — `undefined` читається як оригінал. */
export function copyFieldsOf(table: DocumentTableDto): TableCopyFields {
  return {
    sheetCopyId: table.sheetCopyId ?? null,
    copyOrdinal: table.copyOrdinal,
    displayNameL10n: table.displayNameL10n,
    isCopyable: table.isCopyable,
  };
}

/** Ключ вкладки: оригінал — код аркуша (адреса `?sheet=` не змінилась), копія — `код~id`. */
export function sheetTabKey(sheetCode: string, sheetCopyId: number | null): string {
  return sheetCopyId === null ? sheetCode : `${sheetCode}~${String(sheetCopyId)}`;
}

/**
 * Ключ у `DocumentSummary.sheetStates`: оригінал — `КОД`, копія — `КОД#sheetCopyId`
 * (контракт сервера; відрізняється від ключа вкладки `код~id`).
 */
export function sheetStateKey(sheetCode: string, sheetCopyId: number | null): string {
  return sheetCopyId === null ? sheetCode : `${sheetCode}#${String(sheetCopyId)}`;
}

/** Ключ `sheetStates` належить копії (`КОД#id`), а не окремому аркушу шаблону. */
export function isCopyStateKey(key: string): boolean {
  return /#\d+$/.test(key);
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
  return localized(table.displayNameL10n) || localized(table.sheetNameL10n) || '—';
}
