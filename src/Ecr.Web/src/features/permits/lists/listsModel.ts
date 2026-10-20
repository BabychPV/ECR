import { language, t } from '@/shared/i18n';
import { textOf } from '../typeEditor/labels';
import type { PermitListDto, PermitListUseDto } from './listsApi';

/**
 * Модель екрана «Lists» (PM-08d, PM-08e): рядки будуються з відповіді ОДНОГО ендпоінта `GET /permit-lists` — без читання
 * визначення кожного типу. Нічого з React і мережі.
 */

/** Походження списку; збігається з областю списку в конструкторі типу. */
export type ListKind = 'shared' | 'type' | 'permit';

export const ListKinds: readonly ListKind[] = ['shared', 'type', 'permit'];

/** Стовпець типу, що бере значення зі списку. */
export interface ListUse {
  readonly typeCode: string;
  readonly typeName: string;
  readonly tableCode: string;
  readonly tableName: string;
  readonly columnCode: string;
  readonly columnName: string;
}

export interface ListRow {
  /** Ключ списку з сервера: він же ключ рядка, значення `?panel=` і сегмент адреси значень. */
  readonly id: string;
  readonly kind: ListKind;
  readonly name: string;
  /** Тип-власник для `type`/`permit`; порожньо для спільного. */
  readonly owner: string;
  /** Код тип-власника (куди веде «Open the permit type»); для спільного — перший тип, що ним користується. */
  readonly typeCode: string;
  /** Код довідника зі значеннями (для «лише тип» — прихований). */
  readonly registryCode: string | null;
  /** Кількість значень; `null` — список-таблиця дозволу (значення — рядки дозволу). */
  readonly entries: number | null;
  /** Чи може поточний користувач змінювати значення (рішення сервера: право й область). */
  readonly editable: boolean;
  readonly used: readonly ListUse[];
}

/** «Used by» словами макета: «1 column» / «N columns» / «No column yet». */
export function usedCountText(count: number): string {
  if (count <= 0) return t('permits.lists.used.none');

  return count === 1 ? t('permits.lists.used.one') : t('permits.lists.used.other', { count });
}

/** Підпис походження словом каталогу (ключі — літерали: сторож каталогу читає їх як текст). */
export function kindLabel(kind: ListKind): string {
  switch (kind) {
    case 'shared':
      return t('permits.lists.kind.shared');
    case 'type':
      return t('permits.lists.kind.type');
    case 'permit':
      return t('permits.lists.kind.permit');
  }
}

const KindOrder: Record<ListKind, number> = { shared: 0, type: 1, permit: 2 };

/** Область списку на сервері → походження екрана; невідома область дає `null` (рядок пропускається). */
export function kindOfScope(scope: string): ListKind | null {
  switch (scope) {
    case 'SharedRegistry':
      return 'shared';
    case 'TypeOnly':
      return 'type';
    case 'SamePermitTable':
      return 'permit';
    default:
      return null;
  }
}

function useOf(use: PermitListUseDto, lang: string | undefined): ListUse {
  return {
    typeCode: use.typeCode,
    typeName: textOf(use.typeNameL10n, lang) || use.typeCode,
    tableCode: use.tableCode,
    tableName: textOf(use.tableNameL10n, lang) || use.tableCode,
    columnCode: use.fieldCode,
    columnName: textOf(use.fieldNameL10n, lang) || use.fieldCode,
  };
}

/** Рядки екрана з відповіді сервера: спершу спільні, далі «лише тип» і «таблиця дозволу», усередині — за назвою. */
export function rowsFromLists(lists: readonly PermitListDto[], lang: string | undefined = undefined): ListRow[] {
  const rows: ListRow[] = [];

  for (const list of lists) {
    const kind = kindOfScope(list.scope);
    if (kind === null) continue;
    const used = list.usedBy.map((use) => useOf(use, lang));
    const first = used[0];

    rows.push({
      id: list.key,
      kind,
      name: textOf(list.nameL10n, lang) || list.registryCode || list.key,
      owner: kind === 'shared' ? '' : (first?.typeName ?? list.typeCode ?? ''),
      typeCode: list.typeCode ?? first?.typeCode ?? '',
      registryCode: list.registryCode,
      entries: list.itemCount,
      editable: list.editable,
      used,
    });
  }

  return rows.sort((a, b) => KindOrder[a.kind] - KindOrder[b.kind] || a.name.localeCompare(b.name));
}

export interface ListsStats {
  readonly all: number;
  readonly shared: number;
  readonly type: number;
  readonly permit: number;
}

export function listsStats(rows: readonly ListRow[]): ListsStats {
  return {
    all: rows.length,
    shared: rows.filter((row) => row.kind === 'shared').length,
    type: rows.filter((row) => row.kind === 'type').length,
    permit: rows.filter((row) => row.kind === 'permit').length,
  };
}

export interface ListsFilter {
  readonly needle: string;
  /** Походження з фільтра або показника смуги; порожньо — усі. */
  readonly kind: string;
}

/** Пошук за назвою списку, власником і кодом довідника; походження — рівність. */
export function filterLists(rows: readonly ListRow[], filter: ListsFilter): ListRow[] {
  const needle = filter.needle.trim().toLocaleLowerCase();

  return rows.filter((row) => {
    if (filter.kind !== '' && row.kind !== filter.kind) return false;
    if (needle === '') return true;

    return `${row.name} ${row.owner} ${row.registryCode ?? ''}`.toLocaleLowerCase().includes(needle);
  });
}

/** Значення показника смуги/фільтра як походження або `''`. */
export function kindParam(value: string | null | undefined): string {
  return value !== null && value !== undefined && (ListKinds as readonly string[]).includes(value) ? value : '';
}

/**
 * Підписи нового чи зміненого значення для запиту. Англійська — основа списку «лише для типу» (за нею збігаються значення
 * чернетки типу), тож вона є завжди: користувач англійської міняє її, іншої мови — лише свою, англійська лишається як була
 * (або дорівнює введеному, якщо значення нове).
 */
export function valueLabels(
  text: string,
  existing: Readonly<Record<string, string>> | undefined,
  lang: string = language(),
): Record<string, string> {
  const trimmed = text.trim();
  if (lang === 'en') return { en: trimmed };

  return { en: existing?.en ?? trimmed, [lang]: trimmed };
}

/** Ключ списку зі стовпця форми дозволу: `shared:…` чи `type:…`; для списку-таблиці дозволу `null` (там нічого не додають). */
export function listKeyOf(
  scope: string | null | undefined,
  typeCode: string,
  tableCode: string,
  fieldCode: string,
  registryCode: string | null | undefined,
): string | null {
  if (scope === 'SharedRegistry') return registryCode == null || registryCode === '' ? null : `shared:${registryCode}`;
  if (scope === 'TypeOnly') return `type:${typeCode}:${tableCode}:${fieldCode}`;

  return null;
}
