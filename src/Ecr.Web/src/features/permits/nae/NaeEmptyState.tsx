import type { JSX } from 'react';
import { Button, Stack, Text } from '@mantine/core';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { queryKeys } from '@/api/queryKeys';
import { usePendingLoading } from '@/features/common/usePendingLoading';
import { t } from '@/shared/i18n';
import { showApiError, showDone } from '@/shared/ui/notify';
import { seedNaeRoots } from './naeManualApi';

export interface NaeEmptyStateProps {
  /** Можна створювати вузли без AF (`Registry.EditData`, а для ще не створеного довідника ще й `EditDefinition`). */
  readonly canManual: boolean;
}

/**
 * «Створити регіони з довідників» - кнопка панелі інструментів сторінки; сторінка показує її, лише поки дерево порожнє,
 * бо сервер дозволяє `seed-roots` тільки для порожнього дерева (інакше `422`).
 */
export function NaeSeedRootsButton(): JSX.Element {
  const queryClient = useQueryClient();
  const seed = useMutation({
    mutationFn: seedNaeRoots,
    onSuccess: async (result) => {
      showDone(t('permits.nae.manual.seeded', { count: result.created.length }));
      await queryClient.invalidateQueries({ queryKey: queryKeys.registries.all() });
    },
    onError: async (error: unknown) => {
      // `422 naeTreeNotEmpty`: дерево встигли наповнити - показуємо причину сервера й перечитуємо дерево.
      showApiError(error);
      await queryClient.invalidateQueries({ queryKey: queryKeys.registries.all() });
    },
  });
  const busy = usePendingLoading(seed.isPending);

  return (
    <Button
      size="xs"
      variant="default"
      loading={busy}
      disabled={seed.isPending}
      onClick={() => seed.mutate()}
      title={t('permits.nae.manual.seedHint')}
      data-nae-seed-roots=""
    >
      {t('permits.nae.manual.seed')}
    </Button>
  );
}

/**
 * Порожнє дерево НВ: лише пояснення трьох шляхів наповнення (завантаження з PI AF, імпорт з Excel, вручну). Кнопок тут
 * немає - вони одні, на панелі інструментів сторінки (людина 04.11: «чого кнопки повторюються»).
 */
export function NaeEmptyState({ canManual }: NaeEmptyStateProps): JSX.Element {
  return (
    <Stack gap="xs" role="status" data-nae-empty="">
      <Text fw={600}>{t('permits.nae.manual.emptyTitle')}</Text>
      <Text size="sm">{t('permits.nae.manual.emptyIntro')}</Text>
      <ol>
        <li>
          <Text size="sm">{t('permits.nae.manual.wayAf')}</Text>
        </li>
        <li>
          <Text size="sm">{t('permits.nae.manual.wayExcel')}</Text>
        </li>
        <li>
          <Text size="sm">{t('permits.nae.manual.wayManual')}</Text>
        </li>
      </ol>
      {!canManual && (
        <Text size="sm" c="dimmed">
          {t('permits.nae.import.hintAskAdmin')}
        </Text>
      )}
    </Stack>
  );
}