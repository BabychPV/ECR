import type { JSX } from 'react';
import { Skeleton, Stack, Text } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import { RegistryUsageList } from '@/features/registries/RegistryUsage';
import { t } from '@/shared/i18n';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { columnUsage } from './columnApi';

/**
 * Ключ запиту «де використовується» для колонки шаблону (ФВ-8.14).
 *
 * ⚠ Локальний, а не в спільній фабриці `queryKeys.ts` — той самий вибір, що
 * й для `registryUsageKey`: єдиний споживач тут, у цій панелі.
 */
export function columnUsageKey(columnDefId: number): readonly ['templates', 'columnUsage', number] {
  return ['templates', 'columnUsage', columnDefId] as const;
}

/**
 * «Де використовується» колонка шаблону — переліковує формули й прив'язки
 * методологій, що на неї посилаються (ФВ-8.14).
 *
 * ⚠ Перевикористовує `RegistryUsageList` (`features/registries/RegistryUsage.tsx`)
 * — та сама форма відповіді (`UsageResponse`), той самий рендер списку, той
 * самий фолбек «не використовується ніде» при `total: 0`. Новий компонент
 * списку тут не пишеться навмисно.
 */
export function TemplateColumnUsage({
  columnDefId,
  isDraft = false,
}: {
  readonly columnDefId: number;

  /**
   * ⛔ R-12: версія — чернетка. Залежності формул (`cfg.FormulaDependency`)
   * записує лише ПУБЛІКАЦІЯ, тож у чернетці формули шаблону, що читають
   * колонку, тут не з'являються ніколи — і «Not used anywhere» читалося як
   * «можна видаляти». Чесне пояснення замість мовчазного нуля.
   */
  readonly isDraft?: boolean;
}): JSX.Element {
  const usage = useQuery({
    queryKey: columnUsageKey(columnDefId),
    queryFn: () => columnUsage(columnDefId),
  });

  if (usage.error !== null) {
    return <ErrorAlert error={usage.error} onRetry={() => void usage.refetch()} />;
  }

  if (usage.isPending) {
    return <Skeleton height={120} radius="sm" data-column-usage="pending" />;
  }

  if (!isDraft) {
    return <RegistryUsageList usage={usage.data} />;
  }

  return (
    <Stack gap="xs">
      <Text size="sm" c="dimmed" data-column-usage="draft-note">
        {t('columns.usageDraftNote')}
      </Text>
      <RegistryUsageList usage={usage.data} />
    </Stack>
  );
}
