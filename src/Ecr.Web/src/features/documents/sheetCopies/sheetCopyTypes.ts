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

/**
 * Назва групи БЕЗ номера з показуваної назви сервера.
 *
 * ✎ debt-1109 SHEETNAME-P3 (F13 p218): сервер віддає `displayNameL10n` уже з номером — «<Назва> N» (`SheetCopyNaming.
 * Display`, коли в групі є копії; без копій — назва як є), а клієнт додавав номер ще раз: «Raft 1 1». Номер `N` у кінці
 * знімається тут, а ставить його один раз `sheetTabLabel`.
 */
export function stripCopyOrdinal(name: string, copyOrdinal: number): string {
  const suffix = ` ${String(copyOrdinal)}`;

  return name.endsWith(suffix) && name.length > suffix.length ? name.slice(0, -suffix.length) : name;
}

/** Назва групи аркушів: назва в документі (з показуваної назви, без номера), інакше назва шаблону. */
export function groupNameOf(table: DocumentTableDto, groupHasCopies: boolean): string {
  const display = localized(table.displayNameL10n);

  // Без копій сервер номера не додає — назву «Unit 1», яку дала людина, не чіпаємо.
  return (display === '' || !groupHasCopies ? display : stripCopyOrdinal(display, table.copyOrdinal)) || localized(table.sheetNameL10n) || '—';
}

/** Назва групи всіма мовами без номера — для діалогу перейменування. */
export function groupNameValuesOf(table: DocumentTableDto, groupHasCopies: boolean): Record<string, string> {
  const values = (table.displayNameL10n ?? table.sheetNameL10n).values ?? {};

  return Object.fromEntries(
    Object.entries(values).map(([lang, value]) => [
      lang,
      table.displayNameL10n === null || table.displayNameL10n === undefined || !groupHasCopies
        ? value
        : stripCopyOrdinal(value, table.copyOrdinal),
    ]),
  );
}
