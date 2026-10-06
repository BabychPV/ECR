import type { RegistryDefDto, RegistrySourceKind } from '@/api/types';
import { t } from '@/shared/i18n';
import { localized } from '@/shared/i18n/localized';

/**
 * Модель переліку довідників (`UI-35`, макет `screens-data.js` `/admin/registries`).
 *
 * ⛔ Лише те, що віддає `GET /api/v1/registries`. Агрегати (`RegistryListItem`)
 * малюються, коли прийшли; «cells», «updated by» і дати зміни ОПИСУ сервер не
 * віддає — їх немає (`D15-06`), а «Used in» рахується колонками й шаблонами.
 */

/**
 * Рядок переліку: `RegistryDefDto` + агрегати переліку (`GET /registries`,
 * lane «Аналізу» `ui-registry-def-ext`).
 *
 * ⚠ Поля оголошено тут необов'язковими, а не взято з `schema.d.ts`: контракт
 * приходить сусідньою гілкою, і ця має збиратися й до, і після неї. Відсутнє
 * поле (`undefined`, старий сервер) і `null` (без права / не змінювалися)
 * клієнт розрізняє: колонки без даних не малюються зовсім (`D15-06`).
 * Після злиття контракту перетин стає тотожним `RegistryDefDto`.
 */
export type RegistryListItem = RegistryDefDto & {
  /** Чинних записів сьогодні (UTC). */
  readonly entryCount?: number | null | undefined;
  /** Версія опублікованого опису. */
  readonly definitionVersion?: number | null | undefined;
  /** Коли востаннє змінювалися записи (UTC); `null` — не змінювалися. */
  readonly dataChangedAt?: string | null | undefined;
  /** Скільки колонок шаблонів беруть значення; `null` без `Registry.EditDefinition`. */
  readonly usedInColumns?: number | null | undefined;
  /** У скількох шаблонах; `null` за тих самих умов. */
  readonly usedInTemplates?: number | null | undefined;
  /** Чи є чернетка опису; `null` без `Registry.EditDefinition`. */
  readonly hasDraft?: boolean | null | undefined;
};

/** Чи прийшло поле хоч в одному рядку числом/значенням (`null`/`undefined` — даних немає). */
export function hasAny(
  rows: readonly RegistryListItem[],
  pick: (row: RegistryListItem) => unknown,
): boolean {
  return rows.some((row) => pick(row) !== null && pick(row) !== undefined);
}

/** Чи сервер узагалі віддає поле (на старому сервері його немає в жодному рядку). */
export function hasField(rows: readonly RegistryListItem[], field: keyof RegistryListItem): boolean {
  return rows.some((row) => field in row);
}

/** Показники смуги, що фільтрують перелік (`?stat=`). */
export type RegistryStat = 'temporal' | 'external' | 'changed';

export const RegistryStats: readonly RegistryStat[] = ['temporal', 'external', 'changed'];

/** Записи змінювалися в поточному календарному місяці (UTC) — показник макета «changed this month». */
export function changedThisMonth(registry: RegistryListItem, now: Date = new Date()): boolean {
  const changed = registry.dataChangedAt;
  if (changed === null || changed === undefined) return false;

  return changed.slice(0, 7) === now.toISOString().slice(0, 7);
}

/** Чи довідник ведеться не лише в ECR (D-211: записи External — лише синком з AF). */
export function isSynced(registry: RegistryDefDto): boolean {
  return registry.sourceKind !== 'Local';
}

export function matchesStat(registry: RegistryListItem, stat: RegistryStat | null): boolean {
  if (stat === 'temporal') return registry.isTemporal;
  if (stat === 'external') return isSynced(registry);
  if (stat === 'changed') return changedThisMonth(registry);

  return true;
}

/** Назва довідника мовою інтерфейсу; без перекладу — код (порожньої клітинки не буває). */
export function registryName(registry: RegistryDefDto): string {
  const name = localized(registry.nameL10n);

  return name === '' ? registry.code : name;
}

export function matchesSearch(registry: RegistryDefDto, needle: string): boolean {
  if (needle === '') return true;

  return (
    registry.code.toLowerCase().includes(needle) ||
    registryName(registry).toLowerCase().includes(needle)
  );
}

export function parseStat(value: string | null): RegistryStat | null {
  return RegistryStats.find((stat) => stat === value) ?? null;
}

/**
 * Хто master довідника словами (`ФВ-8.9`, D-211).
 *
 * ⚠ Ключі літералами, а не шаблоном `registries.list.sourceKind.${kind}`: так
 * їх бачить сторож каталогу (`EndpointCoverageTests`) без окремого запису.
 */
export function sourceKindLabel(kind: RegistrySourceKind): string {
  switch (kind) {
    case 'External':
      return t('registries.list.sourceKind.External');
    case 'Hybrid':
      return t('registries.list.sourceKind.Hybrid');
    case 'Local':
      return t('registries.list.sourceKind.Local');
  }
}
