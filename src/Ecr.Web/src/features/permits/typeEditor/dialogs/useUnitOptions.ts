import { useMemo } from 'react';
import { useQuery } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type { UnitRef } from '@/api/types';
import { toUnitOptions, type UnitOption } from './names';

/**
 * Одиниці для діалогів стовпців (Permits, PM-08c).
 *
 * ⚠ Ключ `['units']` той самий, що в панелях методологій і редакторі даних довідника: кеш
 * спільний, другого запиту за переліком не буде. Читає лише хост діалогів — самі діалоги
 * одиниці отримують пропом.
 */
export function useUnitOptions(enabled: boolean): {
  readonly units: readonly UnitOption[];
  readonly isPending: boolean;
} {
  const query = useQuery({
    queryKey: ['units'],
    queryFn: () => apiFetch<UnitRef[]>('/api/v1/units'),
    enabled,
  });

  const units = useMemo(() => toUnitOptions(query.data ?? []), [query.data]);

  return { units, isPending: enabled && query.isPending };
}
