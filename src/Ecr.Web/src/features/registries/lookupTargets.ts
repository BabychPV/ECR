import { useQuery } from '@tanstack/react-query';
import { apiFetch, EcrApiError } from '@/api/client';
import { queryKeys } from '@/api/queryKeys';
import type { RegistryDefDto, RegistryTargetDto } from '@/api/types';
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
export function lookupTargetLabel(registry: Pick<RegistryDefDto, 'code' | 'nameL10n' | 'kind'>): string {
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
 * Опція поточної цілі, якої немає в переліку. Відомості з `GET /registries/by-id/{id}` (`known`) дають назву і код (для
 * групи ще й опис; неактивна - з позначкою); без них (404/403 однаково, без пояснення чому - S18) лишається підпис з id.
 */
function unlistedOption(id: number, known: RegistryTargetDto | null): LookupTargetOption {
  if (known === null) return { value: String(id), label: t('registries.lookupTargetUnlisted', { id: String(id) }) };

  const label = lookupTargetLabel(known);
  const option: LookupTargetOption = {
    value: String(id),
    label: known.isActive ? label : t('registries.lookupTargetInactive', { label }),
  };
  if (known.kind === 'PermitGroup') {
    const description = localized(known.descriptionL10n).trim();
    if (description !== '') option.description = description;
  }

  return option;
}

/** Відомості про поточну ціль за id: лише коли її НЕМАЄ в переліку цілей; відмова (404/403) - `null`, без помилки. */
export function unlistedTargetQuery(id: number): {
  queryKey: ReturnType<typeof queryKeys.registries.target>;
  queryFn: () => Promise<RegistryTargetDto | null>;
  staleTime: number;
  retry: false;
} {
  return {
    queryKey: queryKeys.registries.target(id),
    // ⛔ 404 (немає або заборонено - та сама відповідь) і 403 не розкриваються: підпис лишається «Not in the list».
    queryFn: async () => {
      try {
        return await apiFetch<RegistryTargetDto>(`/api/v1/registries/by-id/${String(id)}`);
      } catch (error) {
        if (error instanceof EcrApiError && (error.problem.status === 404 || error.problem.status === 403)) return null;
        throw error;
      }
    },
    staleTime: 5 * 60_000,
    retry: false,
  };
}

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
  known: RegistryTargetDto | null = null,
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
  const unlisted: LookupTargetOption | null =
    currentId !== null && !registries.some((registry) => registry.id === currentId)
      ? unlistedOption(currentId, known?.id === currentId ? known : null)
      : null;

  if (permits.length === 0 && unlisted === null) return dictionaries;

  const groups: LookupTargetGroup[] = [];
  if (permits.length > 0) groups.push({ group: t('registries.lookupGroup.permits'), items: permits });
  if (dictionaries.length > 0) groups.push({ group: t('registries.lookupGroup.dictionaries'), items: dictionaries });
  if (unlisted !== null) groups.push({ group: t('registries.lookupGroup.unlisted'), items: [unlisted] });

  return groups;
}

/**
 * Відомості про поточну ціль, якої немає в переліку цілей (`GET /registries/by-id/{id}`). Запит ЛІНИВИЙ: лише коли
 * перелік уже приїхав, ціль задана і її в ньому немає; ціль у списку - жодного запиту. Відмова - `null` (підпис з id).
 */
export function useUnlistedTarget(currentId: number | null, targets: readonly RegistryDefDto[] | undefined): RegistryTargetDto | null {
  const outside = currentId !== null && targets !== undefined && !targets.some((registry) => registry.id === currentId);
  const query = useQuery({ ...unlistedTargetQuery(currentId ?? 0), enabled: outside });

  return outside ? (query.data ?? null) : null;
}