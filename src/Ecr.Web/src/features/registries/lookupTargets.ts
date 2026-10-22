import { apiFetch } from '@/api/client';
import { queryKeys } from '@/api/queryKeys';
import type { RegistryDefDto } from '@/api/types';
import { t } from '@/shared/i18n';
import { localized } from '@/shared/i18n/localized';

/** Код реєстру шапки типу дозволу — `PRM_` + код типу (таблиці типу — `PRM_<TYPE>_<TABLE>`, їх у цілях немає). */
const PermitHeaderPrefix = 'PRM_';

/** Чи це реєстр шапки типу дозволу (його записи — дозволи; `Display` запису — номер дозволу). */
export function isPermitHeaderRegistry(code: string): boolean {
  return code.startsWith(PermitHeaderPrefix);
}

/**
 * Запит цілей Lookup: звичайний перелік довідників ПЛЮС шапки опублікованих типів дозволів.
 *
 * ⛔ Окремий ключ і окремий запит: загальний `GET /api/v1/registries` ховає `PRM_*` навмисно (перелік «Довідники»), а
 * конструктор шаблону (поле шапки, колонка) і піцкери документа адресують ціль за ідентифікатором. Права ті самі:
 * `Registry.View` і заборона на довідник.
 */
export function lookupTargetsQuery(): {
  queryKey: ReturnType<typeof queryKeys.registries.lookupTargets>;
  queryFn: () => Promise<RegistryDefDto[]>;
} {
  return {
    queryKey: queryKeys.registries.lookupTargets(),
    queryFn: () => apiFetch<RegistryDefDto[]>('/api/v1/registries?includePermitHeaders=true'),
  };
}

/** Підпис цілі у списку вибору: «Назва (КОД)»; для шапки дозволу — «Permits: Назва (PRM_КОД)». */
export function lookupTargetLabel(registry: RegistryDefDto): string {
  const name = localized(registry.nameL10n);

  return isPermitHeaderRegistry(registry.code)
    ? t('registries.permitHeaderOption', { name, code: registry.code })
    : `${name} (${registry.code})`;
}

export interface LookupTargetOption {
  value: string;
  label: string;
}

/**
 * Дані `Select` вибору цілі Lookup: група «Permits» ПЕРШОЮ (шапки типів дозволів, `PRM_*`), далі «Dictionaries».
 *
 * ⚠ Без дозволів у відповіді (немає опублікованих типів або немає права) - плаский список довідників, як було:
 * порожня група не малюється. Це не помилка і не окреме повідомлення.
 */
export function lookupTargetSelectData(
  registries: readonly RegistryDefDto[],
): LookupTargetOption[] | { group: string; items: LookupTargetOption[] }[] {
  const toOption = (registry: RegistryDefDto): LookupTargetOption => ({
    value: String(registry.id),
    label: lookupTargetLabel(registry),
  });
  const permits = registries.filter((registry) => isPermitHeaderRegistry(registry.code)).map(toOption);
  const dictionaries = registries.filter((registry) => !isPermitHeaderRegistry(registry.code)).map(toOption);

  if (permits.length === 0) return dictionaries;

  return [
    { group: t('registries.lookupGroup.permits'), items: permits },
    { group: t('registries.lookupGroup.dictionaries'), items: dictionaries },
  ];
}