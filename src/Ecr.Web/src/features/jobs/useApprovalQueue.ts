import { useInfiniteQuery, type UseInfiniteQueryResult, type InfiniteData } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type { components } from '@/api/schema';

export type ApprovalQueue = components['schemas']['ApprovalQueueDto'];
export type ApprovalQueueItem = components['schemas']['ApprovalQueueItemDto'];

/** Розмір сторінки: шухляда показує початок черги, решту - кнопкою «Показати ще». */
export const ApprovalQueuePageSize = 50;

/** Опитування, поки шухляда відкрита: подання робить інша людина. Закрита - лише на фокус. */
const OpenedPollMs = 60_000;

/** Свіжість, поки шухляда закрита (лічильник кнопки): не частіше разу на хвилину, крім дій робочого процесу. */
const ClosedStaleMs = 60_000;

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
    // ✎ D-449: той самий ключ читає лічильник кнопки «My tasks» у шапці КОЖНОГО маршруту. Закрита шухляда: свіже
    // 60 с, без опитування - перезапит лише на фокус вікна (якщо минуло 60 с) і після дій робочого процесу
    // (`SheetActions.refresh` скидає ключ). Перехід між маршрутами нового запиту не робить. Відкрита - опитування.
    staleTime: opened ? 0 : ClosedStaleMs,
  });
}

/** Усі завантажені рядки черги підряд (порядок сервера: найдавніші подання першими). */
export function approvalQueueItems(data: InfiniteData<ApprovalQueue, string | null> | undefined): readonly ApprovalQueueItem[] {
  return data?.pages.flatMap((page) => page.items) ?? [];
}

/**
 * Посилання на аркуш документа: період і аркуш - в адресі (ФВ-14.29), інакше відкриється інший аркуш.
 * ✎ D-449: те саме посилання бере блок «Повернуто на доопрацювання» - тому лише три поля, а не рядок черги.
 */
export function approvalQueueHref(item: Pick<ApprovalQueueItem, 'documentId' | 'periodKey' | 'sheetCode'>): string {
  return `/documents/${String(item.documentId)}?periodKey=${String(item.periodKey)}&sheet=${encodeURIComponent(item.sheetCode)}`;
}
