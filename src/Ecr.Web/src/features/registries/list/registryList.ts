import type { RegistryDefDto, RegistrySourceKind } from '@/api/types';
import { t } from '@/shared/i18n';
import { localized } from '@/shared/i18n/localized';

/**
 * Модель переліку довідників (`UI-35`, макет `screens-data.js` `/admin/registries`).
 *
 * ⛔ Лише те, що віддає `GET /api/v1/registries` (`RegistryDefDto`): назва,
 * код, поля, ознаки, хто master. Показники макета «entries», «changed this
 * month», «referenced by cells» і колонки «Entries / Used in / Updated / State»
 * потребують агрегатів, яких у відповіді немає, — їх не малюємо зовсім
 * (`D15-06`), а не показуємо нулями. TODO-контракт — у листі готовності.
 */

/** Показники смуги, що фільтрують перелік (`?stat=`). */
export type RegistryStat = 'temporal' | 'external';

export const RegistryStats: readonly RegistryStat[] = ['temporal', 'external'];

/** Чи довідник ведеться не лише в ECR (D-211: записи External — лише синком з AF). */
export function isSynced(registry: RegistryDefDto): boolean {
  return registry.sourceKind !== 'Local';
}

export function matchesStat(registry: RegistryDefDto, stat: RegistryStat | null): boolean {
  if (stat === 'temporal') return registry.isTemporal;
  if (stat === 'external') return isSynced(registry);

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
