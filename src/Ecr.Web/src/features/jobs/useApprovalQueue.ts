import { useInfiniteQuery, type UseInfiniteQueryResult, type InfiniteData } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type { components } from '@/api/schema';

export type ApprovalQueue = components['schemas']['ApprovalQueueDto'];
export type ApprovalQueueItem = components['schemas']['ApprovalQueueItemDto'];

/** Розмір сторінки: шухляда показує початок черги, решту - кнопкою «Показати ще». */
export const ApprovalQueuePageSize = 50;

/** Опитування, поки шухляда відкрита: подання робить інша людина. Закрита - лише на фокус. */
const OpenedPollMs = 60_000;

/**
 * Черга «До погодження»: аркуші, які викликач може затвердити зараз (`GET /api/v1/me/approval-queue`).
 *
 * ⛔ Окремого `canApprove` немає: сервер уже відфільтрував за правами, роллю кроку і гарантом «не сам подав».
 * ⚠ Сторінка може бути коротшою за `limit` при непорожньому `nextCursor` - ознака продовження лише курсор.
 * Курсор непрозорий; 422 `approvalQueueCursorInvalid` обробляє загальний перегляд помилки (перезапит з початку).
 */
export function useApprovalQueue(opened: boolean): UseInfiniteQueryResult<InfiniteData<ApprovalQueue, string | null>> {
  return useInfiniteQuery({
    queryKey: ['approval-queue', 'mine'],
    initialPageParam: null as string | null,
    queryFn: ({ pageParam }) =>
      apiFetch<ApprovalQueue>(
        `/api/v1/me/approval-queue?limit=${String(ApprovalQueuePageSize)}${
          pageParam === null ? '' : `&cursor=${encodeURIComponent(pageParam)}`
        }`,
      ),
    getNextPageParam: (last) => last.nextCursor ?? undefined,
    refetchOnWindowFocus: true,
    refetchInterval: opened ? OpenedPollMs : false,
    staleTime: opened ? 0 : 30_000,
  });
}

/** Усі завантажені рядки черги підряд (порядок сервера: найдавніші подання першими). */
export function approvalQueueItems(data: InfiniteData<ApprovalQueue, string | null> | undefined): readonly ApprovalQueueItem[] {
  return data?.pages.flatMap((page) => page.items) ?? [];
}

/** Посилання на аркуш документа: період і аркуш - в адресі (ФВ-14.29), інакше відкриється інший аркуш. */
export function approvalQueueHref(item: ApprovalQueueItem): string {
  return `/documents/${String(item.documentId)}?periodKey=${String(item.periodKey)}&sheet=${encodeURIComponent(item.sheetCode)}`;
}
