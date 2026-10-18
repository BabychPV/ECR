import { t } from '@/shared/i18n';
import { localized } from '@/shared/i18n/localized';
import type { PermitTypeListItem, PermitTypeTableSummary } from '../api/types';

/**
 * Чиста модель переліку типів дозволів (PM-08a): назви, підзаголовок, стан, показники смуги,
 * фільтрація. Нічого з React і мережі — усе перевіряється без рендера.
 */

/** Категорії типів, які пропонує майстер і фільтр макета (`Other` у фільтрі — лише коли такі типи є). */
export const PermitCategories = ['Air', 'Water', 'Waste', 'Other'] as const;

/**
 * Категорія словом каталогу; чужа категорія з сервера показується як є.
 * ⚠ Ключі — літерали в `t(...)`: сторож каталогу читає їх як текст.
 */
export function categoryLabel(category: string): string {
  switch (category) {
    case 'Air':
      return t('permits.category.Air');
    case 'Water':
      return t('permits.category.Water');
    case 'Waste':
      return t('permits.category.Waste');
    case 'Other':
      return t('permits.category.Other');
    default:
      return category;
  }
}

/** Показники, які фільтрують перелік (`?stat=`). */
export type TypesStat = 'draft';

export interface TypesStats {
  /** Типів усього. */
  readonly types: number;
  /** Таблиць у всіх типах. */
  readonly tables: number;
  /** Типів зі змінами, яких немає в опублікованому. */
  readonly draft: number;
}

export interface TypesFilter {
  /** Пошуковий рядок (будь-який регістр). */
  readonly needle: string;
  /** Категорія або порожньо. */
  readonly category: string;
  /** Обраний показник смуги або `null`. */
  readonly stat: string | null;
}

/** Мовний словник сервера (`{ en, ru, kz }`) у форму, яку розуміє `localized`. */
function text(map: Readonly<Record<string, string>> | null | undefined, lang?: string): string {
  return localized({ values: map ?? null }, lang);
}

/** Назва типу мовою користувача (запасні мови — як у `localized`); порожню замінює код. */
export function typeName(item: PermitTypeListItem, lang?: string): string {
  const name = text(item.nameL10n, lang);

  return name.length > 0 ? name : item.code;
}

/**
 * Підзаголовок назви (макет: російська назва під англійською). Показується назва іншою мовою — `ru`,
 * а коли інтерфейс і так російський, `en`; якщо її немає або вона збігається з основною — код.
 */
export function typeSubtitle(item: PermitTypeListItem, lang?: string): string {
  const main = typeName(item, lang);
  const other = item.nameL10n[lang === 'ru' ? 'en' : 'ru'] ?? '';

  return other.length > 0 && other !== main ? other : item.code;
}

/** Тип має зміни, яких немає в опублікованій версії (чернетка нового типу теж). */
export function hasDraftChanges(item: PermitTypeListItem): boolean {
  return item.hasUnpublishedChanges || item.status === 'Draft';
}

/** Показники смуги над переліком. */
export function listStats(items: readonly PermitTypeListItem[]): TypesStats {
  return {
    types: items.length,
    tables: items.reduce((sum, item) => sum + item.tables.length, 0),
    draft: items.filter(hasDraftChanges).length,
  };
}

function tableName(table: PermitTypeTableSummary, lang?: string): string {
  const name = text(table.nameL10n, lang);

  return name.length > 0 ? name : table.code;
}

/** «Emission limits (6) · Discharge volumes (12)» — таблиці типу з кількістю стовпців. */
export function tablesText(item: PermitTypeListItem, lang?: string): string {
  return item.tables.map((table) => `${tableName(table, lang)} (${String(table.columnCount)})`).join(' · ');
}

/** Пошук за назвою будь-якою мовою й кодом. */
export function matchesNeedle(item: PermitTypeListItem, needle: string): boolean {
  const query = needle.trim().toLocaleLowerCase();
  if (query.length === 0) return true;

  const haystack = [item.code, ...Object.values(item.nameL10n)].join(' ').toLocaleLowerCase();

  return haystack.includes(query);
}

/** Перелік після пошуку, категорії й показника. Невідомий показник нічого не відсіює. */
export function filterTypes(items: readonly PermitTypeListItem[], filter: TypesFilter): PermitTypeListItem[] {
  return items.filter(
    (item) =>
      matchesNeedle(item, filter.needle) &&
      (filter.category.length === 0 || item.category === filter.category) &&
      (filter.stat !== 'draft' || hasDraftChanges(item)),
  );
}

/** Категорії для фільтра: три з макета, `Other` — коли хоч один тип у ній; чужі з сервера не губляться. */
export function categoryOptions(items: readonly PermitTypeListItem[]): string[] {
  const present = new Set(items.map((item) => item.category));
  const base = PermitCategories.filter((category) => category !== 'Other' || present.has('Other'));
  const extra = [...present].filter((category) => !(PermitCategories as readonly string[]).includes(category));

  return [...base, ...extra.sort()];
}
