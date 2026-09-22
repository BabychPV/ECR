import type { JSX } from 'react';
import { Skeleton } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import { RegistryUsageList } from '@/features/registries/RegistryUsage';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { methodologyConstantUsage } from './api';

/**
 * Ключ запиту «де використовується» для константи версії (ФВ-8.14).
 *
 * ⚠ Локальний, а не в спільній фабриці `queryKeys.ts` — той самий вибір, що
 * й для `registryUsageKey`: єдиний споживач тут, у цій панелі.
 */
export function constantUsageKey(
  versionId: number,
  code: string,
): readonly ['methodologies', 'constantUsage', number, string] {
  return ['methodologies', 'constantUsage', versionId, code] as const;
}

/**
 * «Де використовується» константа версії — переліковує формули, що на неї
 * посилаються (ФВ-8.14).
 *
 * ⚠ Перевикористовує `RegistryUsageList` (`features/registries/RegistryUsage.tsx`)
 * — та сама форма відповіді (`UsageResponse`), той самий рендер списку, той
 * самий фолбек «не використовується ніде» при `total: 0`. Новий компонент
 * списку тут не пишеться навмисно.
 */
export function MethodologyConstantUsage({
  methodologyId,
  versionId,
  code,
}: {
  readonly methodologyId: number;
  readonly versionId: number;
  readonly code: string;
}): JSX.Element {
  const usage = useQuery({
    queryKey: constantUsageKey(versionId, code),
    queryFn: () => methodologyConstantUsage(methodologyId, versionId, code),
  });

  if (usage.error !== null) {
    return <ErrorAlert error={usage.error} onRetry={() => void usage.refetch()} />;
  }

  if (usage.isPending) {
    return <Skeleton height={120} radius="sm" data-constant-usage="pending" />;
  }

  return <RegistryUsageList usage={usage.data} />;
}
