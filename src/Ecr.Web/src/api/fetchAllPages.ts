import { apiFetch } from './client';

/** Курсорна сторінка сервера (`PagedResult<T>`): усі переліки API мають цю форму. */
export interface CursorPage<T> {
  items: T[];
  /** Курсор наступної сторінки; `null` — кінець. */
  nextCursor: string | null;
  /** Загальна кількість; `null`, якщо сервер її не рахує. */
  totalCount: number | null;
}

/** Розмір сторінки — стеля сервера (`CursorRequest.MaxLimit`). */
export const AllPagesPageSize = 500;

/**
 * Скільки сторінок іти, перш ніж зупинитися й чесно сказати «є ще» (`nextCursor` не `null`).
 *
 * ⚠ Запобіжник, а не бізнес-межа: 20 × 500 = 10 000 записів — на порядки більше за довідкові переліки, які
 * сюди ходять (шаблони, версії шаблону, користувачі, документи).
 */
export const AllPagesMaxPages = 20;

interface FetchAllPagesOptions {
  /** Скасування (TanStack Query передає `signal` у `queryFn`): покинутий екран не дочитує решту сторінок. */
  readonly signal?: AbortSignal | undefined;
  readonly pageSize?: number;
  readonly maxPages?: number;
}

/**
 * УСІ сторінки курсорного переліку — у формі однієї сторінки (PSS-19c).
 *
 * ⛔ Екрани просили одну сторінку (`?limit=100`, `?limit=200`, `?limit=500`) і показували те, що прийшло: 101-й
 * шаблон чи 201-й користувач просто не існував для вибору, і ніщо на екрані не казало, що перелік обрізано
 * (`X-07` — той самий клас, що вже виправлено для проєктів, `fetchAllProjects`).
 *
 * ⚠ Форма відповіді — та сама, що в однієї сторінки: споживачі (`.data?.items`) не змінюються, а спільний ключ кешу
 * не отримує іншої форми. `nextCursor` не `null` лише тоді, коли спрацював запобіжник `maxPages` або курсор не
 * рухається — тобто перелік справді неповний, і викликач може це сказати.
 *
 * @param path Адреса переліку; власні параметри запиту (`?q=…`) допустимі — `limit`/`cursor` додаються до них.
 */
export async function fetchAllPages<T>(path: string, options: FetchAllPagesOptions = {}): Promise<CursorPage<T>> {
  const pageSize = options.pageSize ?? AllPagesPageSize;
  const maxPages = options.maxPages ?? AllPagesMaxPages;
  const separator = path.includes('?') ? '&' : '?';
  const init: RequestInit | undefined = options.signal === undefined ? undefined : { signal: options.signal };

  const items: T[] = [];
  const seen = new Set<string>();
  // ⚠ Запис, що прийшов удруге (курсор пішов колом A→B→A, або сторінки перекрились, поки хтось додавав записи), —
  // не другий запис: перелік вибору з двома однаковими пунктами гірший за коротший. Ключ — `id`, якщо він є.
  const ids = new Set<unknown>();
  let cursor: string | null = null;
  let totalCount: number | null = null;

  for (let page = 0; page < maxPages; page += 1) {
    const query: string = cursor === null ? '' : `&cursor=${encodeURIComponent(cursor)}`;
    const next: CursorPage<T> = await apiFetch<CursorPage<T>>(`${path}${separator}limit=${String(pageSize)}${query}`, init);

    for (const item of next.items) {
      const id = (item as { id?: unknown } | null)?.id;
      if (id !== undefined && id !== null) {
        if (ids.has(id)) continue;
        ids.add(id);
      }
      items.push(item);
    }
    totalCount = next.totalCount ?? totalCount;
    cursor = next.nextCursor ?? null;

    if (cursor === null || cursor.length === 0) return { items, nextCursor: null, totalCount };

    // Курсор, що не рухається, — зламаний сервер, а не «ще сторінка»: зупиняємось і кажемо «неповно».
    if (seen.has(cursor)) break;
    seen.add(cursor);
  }

  return { items, nextCursor: cursor, totalCount };
}
