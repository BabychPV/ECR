import { useCallback, useEffect, useState } from 'react';

/** Запас знизу: наступна сторінка проситься, коли до сторожа лишається стільки пікселів. */
export const InfiniteScrollAheadPx = 400;

/** Скільки сторінок поспіль хук бере САМ, поки сторож не виходив за межі вікна (порожні сторінки, 4K-екран). */
export const MaxAutoFillChain = 5;

/** Після стількох рядків автодовантаження зупиняється (W-3: без віртуалізації, `D-332`); далі — кнопка. */
export const MaxAutoRows = 1000;

/** Те, що потрібно від `useInfiniteQuery` (структурно сумісне з `UseInfiniteQueryResult`). */
export interface InfinitePager {
  readonly hasNextPage: boolean;
  readonly isFetching: boolean;
  readonly isFetchingNextPage: boolean;
  readonly isFetchNextPageError: boolean;
  readonly error: unknown;
  readonly fetchNextPage: (options?: { cancelRefetch?: boolean }) => Promise<unknown>;
}

/** Чому автодовантаження не діє: немає IO, вичерпано ланцюг, досягнуто стелі рядків. */
export type AutoPause = 'unsupported' | 'chain' | 'rows' | null;

export interface InfiniteScroll {
  /** Ref-колбек вузла-сторожа під останнім рядком. */
  readonly sentinelRef: (node: HTMLElement | null) => void;
  readonly pause: AutoPause;
  /** Наступна сторінка дією людини (кнопка, «Повторити»); знімає паузу ланцюга. */
  readonly loadMore: () => void;
}

/**
 * W-3 (рішення PO 11.11): нескінченна прокрутка переліку.
 *
 * ⛔ Спостерігач живе ЛИШЕ коли запиту немає (`!isFetching`) і є що брати: при перетині він від'єднується ДО виклику, а
 * `cancelRefetch: false` повертає проміс запиту, що вже йде, — дубль-запитів немає.
 * ⚠ Після кожної сторінки спостерігач створюється НАНОВО: браузер одразу повідомляє новому про поточний перетин, тож
 * рядки, що не заповнили вікно, самі тягнуть наступну сторінку (прийом `SheetTables.tsx:295-302`).
 * ⚠ Сторож вийшов за межі вікна — ланцюг з нуля: далі сторінки бере прокрутка людини.
 */
export function useInfiniteScroll(pager: InfinitePager, shown: number): InfiniteScroll {
  const [sentinel, setSentinel] = useState<HTMLElement | null>(null);
  const [streak, setStreak] = useState(0);
  const { hasNextPage, isFetching, isFetchNextPageError, fetchNextPage } = pager;

  const pause: AutoPause =
    typeof IntersectionObserver === 'undefined'
      ? 'unsupported'
      : shown >= MaxAutoRows
        ? 'rows'
        : streak >= MaxAutoFillChain
          ? 'chain'
          : null;
  const ready = hasNextPage && !isFetching && !isFetchNextPageError && pause === null;

  useEffect(() => {
    if (sentinel === null || !ready) return undefined;

    const observer = new IntersectionObserver(
      (entries) => {
        if (!entries.some((entry) => entry.isIntersecting)) {
          setStreak(0);
          return;
        }

        observer.disconnect();
        setStreak((value) => value + 1);
        void fetchNextPage({ cancelRefetch: false });
      },
      { rootMargin: `0px 0px ${String(InfiniteScrollAheadPx)}px 0px` },
    );
    observer.observe(sentinel);

    return () => observer.disconnect();
  }, [sentinel, ready, fetchNextPage]);

  const loadMore = useCallback(() => {
    setStreak(0);
    void fetchNextPage({ cancelRefetch: false });
  }, [fetchNextPage]);

  return { sentinelRef: setSentinel, pause, loadMore };
}
