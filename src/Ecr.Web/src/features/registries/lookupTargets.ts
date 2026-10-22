import { apiFetch } from '@/api/client';
import { queryKeys } from '@/api/queryKeys';
import type { RegistryDefDto } from '@/api/types';
import { t } from '@/shared/i18n';
import { localized } from '@/shared/i18n/localized';

/** Вид цілі Lookup, який визначає СЕРВЕР (`RegistryDefDto.kind`); поля немає — звичайний довідник. */
export type LookupTargetKind = NonNullable<RegistryDefDto['kind']>;

/**
 * Вид цілі за ознакою сервера.
 *
 * ⛔ НЕ за префіксом коду (`PRM_`/`PRMG_`): вид — відповідь сервера (`RegistryDefDto.kind`), а довідник, що випадково
 * зветься `PRM_…`, лишається довідником. Відсутнє поле (старий сервер, заглушка) — `Dictionary`.
 */
export function lookupTargetKind(registry: Pick<RegistryDefDto, 'kind'>): LookupTargetKind {
  return registry.kind ?? 'Dictionary';
}

/** Чи ціль належить групі «Permits» (шапка типу дозволу або група типів): її записи — дозволи. */
export function isPermitTarget(registry: Pick<RegistryDefDto, 'kind'>): boolean {
  return lookupTargetKind(registry) !== 'Dictionary';
}

/** Чи записи цілі читаються у ВІКНІ проєкту документа (`?documentId=`), а не на дату (`?asOf=`): шапка і група. */
export function usesDocumentWindow(registry: Pick<RegistryDefDto, 'kind'>): boolean {
  return isPermitTarget(registry);
}

/**
 * Запит цілей Lookup: звичайний перелік довідників ПЛЮС шапки опублікованих типів дозволів і групи типів.
 *
 * ⛔ Окремий ключ і окремий запит: загальний `GET /api/v1/registries` ховає `PRM_*`/`PRMG_*` навмисно (перелік
 * «Довідники»), а конструктор шаблону (поле шапки, колонка) і піцкери документа адресують ціль за ідентифікатором.
 * Права ті самі: `Registry.View` і заборона на довідник.
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

  return lookupTargetKind(registry) === 'PermitHeader'
    ? t('registries.permitHeaderOption', { name, code: registry.code })
    : `${name} (${registry.code})`;
}

export interface LookupTargetOption {
  value: string;
  label: string;
  /** Другий рядок опції (лише група типів дозволів): опис групи мовою користувача; без опису поля немає. */
  description?: string;
}

export interface LookupTargetGroup {
  group: string;
  items: LookupTargetOption[];
}

export type LookupTargetData = LookupTargetOption[] | LookupTargetGroup[];

/**
 * Дані `Select` вибору цілі Lookup: група «Permits» ПЕРШОЮ (шапки типів дозволів і групи типів — за `kind` сервера),
 * далі «Dictionaries».
 *
 * ⚠ Без дозволів у відповіді (немає опублікованих типів або немає права) - плаский список довідників, як було:
 * порожня група не малюється. Це не помилка і не окреме повідомлення.
 *
 * ⚠ `currentId` - ПОТОЧНА ціль колонки/поля. Її може не бути в переліку (групу деактивовано, права немає, довідник
 * забрали): без опції `Select` показав би порожнє значення, і автор шаблону подумав би, що ціль не задано. Тоді в кінці
 * з'являється окрема опція з ідентифікатором - вона лишає значення видимим і не дає мовчки його втратити.
 */
export function lookupTargetSelectData(
  registries: readonly RegistryDefDto[],
  currentId: number | null = null,
): LookupTargetData {
  const toOption = (registry: RegistryDefDto): LookupTargetOption => {
    const option: LookupTargetOption = { value: String(registry.id), label: lookupTargetLabel(registry) };
    if (lookupTargetKind(registry) === 'PermitGroup') {
      const description = localized(registry.descriptionL10n).trim();
      if (description !== '') option.description = description;
    }

    return option;
  };
  const permits = registries.filter(isPermitTarget).map(toOption);
  const dictionaries = registries.filter((registry) => !isPermitTarget(registry)).map(toOption);
  const unlisted =
    currentId !== null && !registries.some((registry) => registry.id === currentId)
      ? { value: String(currentId), label: t('registries.lookupTargetUnlisted', { id: String(currentId) }) }
      : null;

  if (permits.length === 0 && unlisted === null) return dictionaries;

  const groups: LookupTargetGroup[] = [];
  if (permits.length > 0) groups.push({ group: t('registries.lookupGroup.permits'), items: permits });
  if (dictionaries.length > 0) groups.push({ group: t('registries.lookupGroup.dictionaries'), items: dictionaries });
  if (unlisted !== null) groups.push({ group: t('registries.lookupGroup.unlisted'), items: [unlisted] });

  return groups;
}
