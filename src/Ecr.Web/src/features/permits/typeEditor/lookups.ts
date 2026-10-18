import { useQuery, type UseQueryResult } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import { queryKeys } from '@/api/queryKeys';
import type { RegistryDefDto, UnitRef } from '@/api/types';

/*
 * Довідні дані інспектора (Permits, PM-08b): одиниці виміру й спільні довідники. Ті самі запити й
 * ключі, що в сітці та шапці документа, тож кеш спільний.
 */

const HourMs = 60 * 60 * 1000;

export interface Units {
  readonly query: UseQueryResult<UnitRef[]>;
  readonly list: readonly UnitRef[];
  /** Код одиниці за ідентифікатором; невідома — порожньо: голе число поруч зі значенням читається як одиниця. */
  readonly codeOf: (unitId: number) => string;
}

export function useUnits(): Units {
  const query = useQuery({
    queryKey: ['units'],
    queryFn: () => apiFetch<UnitRef[]>('/api/v1/units'),
    staleTime: HourMs,
  });
  const list = query.data ?? [];
  return { query, list, codeOf: (unitId) => list.find((unit) => unit.id === unitId)?.code ?? '' };
}

/** Спільні довідники для списку «A shared registry». Приховані реєстри типів сервер не віддає. */
export function useSharedRegistries(enabled: boolean): UseQueryResult<RegistryDefDto[]> {
  return useQuery({
    queryKey: queryKeys.registries.list(),
    queryFn: () => apiFetch<RegistryDefDto[]>('/api/v1/registries'),
    enabled,
    retry: false,
  });
}
