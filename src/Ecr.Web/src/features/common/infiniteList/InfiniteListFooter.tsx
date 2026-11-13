import { useEffect, useRef, useState, type JSX } from 'react';
import { Box, Button, Group, Text } from '@mantine/core';
import { usePendingLoading } from '@/features/common/usePendingLoading';
import { t } from '@/shared/i18n';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { useInfiniteScroll, type InfinitePager } from './useInfiniteScroll';

export interface InfiniteListFooterProps {
  readonly pager: InfinitePager;
  /** Скільки рядків уже показано (після злиття сторінок). */
  readonly shown: number;
  /** Скільки всього, якщо сервер знає (`totalCount` першої сторінки); інакше `null`. */
  readonly total?: number | null | undefined;
}

/**
 * W-3 (рішення PO 11.11): підвал переліку з нескінченною прокруткою — сторож, рядок стану (live-регіон), пауза,
 * відмова з «Повторити» і кнопка «Load more» (клавіатура, середовища без `IntersectionObserver`; `KIT.md` §6.5).
 *
 * ⚠ Викликач ставить `key` = ключ видачі: новий фільтр — новий підвал (лічильник ланцюга з нуля).
 */
export function InfiniteListFooter({ pager, shown, total }: InfiniteListFooterProps): JSX.Element {
  const { sentinelRef, pause, loadMore } = useInfiniteScroll(pager, shown);
  // ФВ-14.26: спінер лише після порогу (а не з першого кадру) — `usePendingLoading`.
  const loading = usePendingLoading(pager.isFetchingNextPage);
  const status = useRef<HTMLParagraphElement | null>(null);
  const [buttonFocused, setButtonFocused] = useState(false);
  const { hasNextPage } = pager;

  // ⚠ Остання сторінка прибирає кнопку з-під фокуса клавіатури: фокус — на рядок стану, а не на `body`.
  useEffect(() => {
    if (!hasNextPage && buttonFocused) {
      status.current?.focus();
      setButtonFocused(false);
    }
  }, [hasNextPage, buttonFocused]);

  const text =
    shown === 0
      ? ''
      : hasNextPage
        ? typeof total === 'number'
          ? t('common.shownOf', { shown, total })
          : t('common.shownSoFar', { shown })
        : t('list.allShown', { shown });

  return (
    <Box mt="md" data-infinite-footer="" data-auto-pause={pause ?? 'none'}>
      <div ref={sentinelRef} aria-hidden="true" data-infinite-sentinel="" style={{ height: 1 }} />
      {pager.isFetchNextPageError && <ErrorAlert error={pager.error} onRetry={loadMore} />}
      <Group gap="sm">
        <Text ref={status} tabIndex={-1} size="sm" c="dimmed" role="status" data-infinite-status="">
          {text}
        </Text>
        {hasNextPage && (pause === 'chain' || pause === 'rows') && (
          <Text size="sm" c="dimmed" data-infinite-paused="">
            {t('list.autoPaused')}
          </Text>
        )}
        {hasNextPage && !pager.isFetchNextPageError && (
          <Button
            variant="default"
            loading={loading}
            onClick={loadMore}
            onFocus={() => setButtonFocused(true)}
            onBlur={() => setButtonFocused(false)}
            data-load-more=""
          >
            {t('documents.more')}
          </Button>
        )}
      </Group>
    </Box>
  );
}
