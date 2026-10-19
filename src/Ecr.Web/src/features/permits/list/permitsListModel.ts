import { t } from '@/shared/i18n';
import { localized } from '@/shared/i18n/localized';
import type { PermitListItem } from '../api/types';
import { PermitsPaths } from '../shell/permitPaths';

/**
 * Чиста модель переліку дозволів (PM-07, екран «Permits» макета `screens-permits.js` §1): назва типу,
 * показники смуги, фільтрація, доступні дії рядка. Нічого з React і мережі: усе перевіряється без рендера.
 *
 * ⛔ Стан дозволу (`Draft/Active/Expired`) і `endsInDays` рахує сервер на дату `asOf`; клієнт їх лише читає.
 */

/** Показник «закінчуються протягом 120 днів» макета (`expiring`). */
export const EndingSoonDays = 120;

/** Показники смуги, які фільтрують перелік (`?stat=`). */
export const PermitStats = ['active', 'ending', 'free'] as const;
export type PermitStat = (typeof PermitStats)[number];

export function asPermitStat(value: string | null | undefined): PermitStat | null {
  return (PermitStats as readonly (string | null | undefined)[]).includes(value) ? (value as PermitStat) : null;
}

/** Стани для фільтра (порядок макета: Active, Draft, Expired). */
export const FilterStates = ['Active', 'Draft', 'Expired'] as const;

export interface PermitsStats {
  readonly all: number;
  readonly active: number;
  /** Чинні, що закінчуються протягом {@link EndingSoonDays} днів. */
  readonly ending: number;
  /** Не закінчені й не прочитані жодним аркушем. */
  readonly free: number;
}

export interface PermitsFilter {
  /** Пошуковий рядок: номер, розташування, назва типу. */
  readonly needle: string;
  /** Код типу або порожньо. */
  readonly type: string;
  /** `Draft`/`Active`/`Expired` або порожньо. */
  readonly state: string;
  readonly stat: PermitStat | null;
}

/** Назва типу мовою користувача; порожню замінює код. */
export function permitTypeName(item: PermitListItem, lang?: string): string {
  const name = localized({ values: item.typeNameL10n }, lang);

  return name.length > 0 ? name : item.typeCode;
}

export function isEndingSoon(item: PermitListItem): boolean {
  return item.status === 'Active' && item.endsInDays !== null && item.endsInDays <= EndingSoonDays;
}

export function isNotRead(item: PermitListItem): boolean {
  return item.status !== 'Expired' && item.boundSheetCount === 0;
}

export function listStats(items: readonly PermitListItem[]): PermitsStats {
  return {
    all: items.length,
    active: items.filter((item) => item.status === 'Active').length,
    ending: items.filter(isEndingSoon).length,
    free: items.filter(isNotRead).length,
  };
}

function matchesStat(item: PermitListItem, stat: PermitStat | null): boolean {
  switch (stat) {
    case 'active':
      return item.status === 'Active';
    case 'ending':
      return isEndingSoon(item);
    case 'free':
      return isNotRead(item);
    case null:
      return true;
  }
}

/** Пошук за номером, розташуванням і назвою типу будь-якою мовою (макет: номер, розташування, тип). */
export function matchesNeedle(item: PermitListItem, needle: string): boolean {
  const query = needle.trim().toLocaleLowerCase();
  if (query.length === 0) return true;

  const haystack = [item.number, item.location ?? '', item.typeCode, ...Object.values(item.typeNameL10n)]
    .join(' ')
    .toLocaleLowerCase();

  return haystack.includes(query);
}

export function filterPermits(items: readonly PermitListItem[], filter: PermitsFilter): PermitListItem[] {
  return items.filter(
    (item) =>
      matchesNeedle(item, filter.needle) &&
      (filter.type.length === 0 || item.typeCode === filter.type) &&
      (filter.state.length === 0 || item.status === filter.state) &&
      matchesStat(item, filter.stat),
  );
}

/** Типи, що є в переліку (а не весь довідник типів): фільтр не пропонує порожніх варіантів. */
export function typeOptions(items: readonly PermitListItem[], lang?: string): { value: string; label: string }[] {
  const seen = new Map<string, string>();
  for (const item of items) {
    if (!seen.has(item.typeCode)) seen.set(item.typeCode, permitTypeName(item, lang));
  }

  return [...seen.entries()]
    .map(([value, label]) => ({ value, label }))
    .sort((a, b) => a.label.localeCompare(b.label));
}

/** Дія рядка за станом. */
export type PermitAction = 'activate' | 'delete' | 'renew' | 'end';

/**
 * Дії, які сервер допускає зі станом: чернетку активують чи видаляють; чинний поновлюють чи завершують
 * достроково; закінчений лише поновлюють (`ECR-PRM-4096` `notActive`/`notDraft`/`notRenewable`). Невідомий
 * стан не пропонує нічого: дія, яка гарантовано дасть відмову, не показується.
 */
export function actionsFor(status: string): readonly PermitAction[] {
  switch (status) {
    case 'Draft':
      return ['activate', 'delete'];
    case 'Active':
      return ['renew', 'end'];
    case 'Expired':
      return ['renew'];
    default:
      return [];
  }
}

/** Підпис дії. ⚠ Ключі — літерали: сторож каталогу читає їх як текст. */
export function actionLabel(action: PermitAction): string {
  switch (action) {
    case 'activate':
      return t('permits.list.action.activate');
    case 'delete':
      return t('permits.list.action.delete');
    case 'renew':
      return t('permits.list.action.renew');
    case 'end':
      return t('permits.list.action.end');
  }
}

/** Скільки живих рядків і таблиць: «43 in 2 tables». */
export function rowsText(item: PermitListItem): string {
  return item.tableCount === 1
    ? t('permits.list.rowsOne', { rows: item.rowCount })
    : t('permits.list.rowsMany', { rows: item.rowCount, tables: item.tableCount });
}

/** «3 sheet tables»; без читачів — `null` (D15-06: порожнє не малюється). */
export function readByText(item: PermitListItem): string | null {
  if (item.boundSheetCount === 0) return null;

  return item.boundSheetCount === 1
    ? t('permits.list.readByOne', { count: item.boundSheetCount })
    : t('permits.list.readByMany', { count: item.boundSheetCount });
}

/** Адреса сторінки дозволу (`/admin/permits/:id`). */
export function permitHref(id: number): string {
  return `${PermitsPaths.permits}/${String(id)}`;
}
