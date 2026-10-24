import { useInfiniteQuery, type UseInfiniteQueryResult, type InfiniteData } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type { components } from '@/api/schema';

export type ReturnedSheets = components['schemas']['ReturnedSheetsDto'];
export type ReturnedSheetItem = components['schemas']['ReturnedSheetItemDto'];

/** Розмір сторінки: шухляда показує початок переліку, решту - кнопкою «Показати ще». */
export const ReturnedSheetsPageSize = 50;

/** Опитування, поки шухляда відкрита: повертає інша людина. Закрита - лише на фокус. */
const OpenedPollMs = 60_000;

/**
 * ✎ D-449: «Повернуто на доопрацювання» - аркуші, які погоджувач повернув і над якими працюю я
 * (`GET /api/v1/me/returned-sheets`).
 *
 * ⛔ Чия задача, вирішує сервер (власник документа або учасник із наданням Write/Submit, видимий аркуш); клієнт не
 * фільтрує. Повторне подання прибирає рядок на сервері - тут лише перезапит (ключ скидає `SheetActions`).
 * ⚠ Ключ `['returned-sheets', 'mine']` спільний для блоку шухляди й лічильника кнопки: один запит на обидва.
 */
export function useReturnedSheets(opened: boolean): UseInfiniteQueryResult<InfiniteData<ReturnedSheets, string | null>> {
  return useInfiniteQuery({
    queryKey: ['returned-sheets', 'mine'],
    initialPageParam: null as string | null,
    queryFn: ({ pageParam }) =>
      apiFetch<ReturnedSheets>(
        `/api/v1/me/returned-sheets?limit=${String(ReturnedSheetsPageSize)}${
          pageParam === null ? '' : `&cursor=${encodeURIComponent(pageParam)}`
        }`,
      ),
    getNextPageParam: (last) => last.nextCursor ?? undefined,
    refetchOnWindowFocus: true,
    refetchInterval: opened ? OpenedPollMs : false,
    staleTime: opened ? 0 : 30_000,
  });
}

/** Усі завантажені рядки підряд (порядок сервера: найдавніші повернення першими). */
export function returnedSheetItems(data: InfiniteData<ReturnedSheets, string | null> | undefined): readonly ReturnedSheetItem[] {
  return data?.pages.flatMap((page) => page.items) ?? [];
}
