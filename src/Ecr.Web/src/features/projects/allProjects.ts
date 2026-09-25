import { apiFetch } from '@/api/client';
import type { PagedProjects } from '@/api/types';

/** Розмір сторінки — стеля сервера (`CursorRequest.MaxLimit`). */
const PageSize = 500;

/**
 * Скільки сторінок іти, перш ніж зупинитися й чесно сказати «є ще».
 *
 * ⚠ Запобіжник від нескінченного циклу на зламаному курсорі, а не бізнес-межа:
 * 20 × 500 = 10 000 проєктів — на порядки більше за будь-який майданчик.
 */
const MaxPages = 20;

/**
 * УСІ видимі проєкти — для вибору проєкту (`X-07`).
 *
 * ⛔ Екрани просили `?limit=200` і показували те, що прийшло: 201-й проєкт
 * просто не існував для вибору, і ніщо на екрані не казало, що перелік
 * обрізано. Відповідь курсорна, тож тут — прохід сторінками до кінця.
 *
 * ⚠ Форма відповіді та сама (`PagedProjects`), і ключ кешу той самий
 * (`['projects']`): сусідні екрани, що читають його, отримують той самий об'єкт.
 * `nextCursor` не `null` лише тоді, коли спрацював запобіжник `MaxPages` —
 * тобто перелік справді неповний, і викликач може це сказати.
 */
export async function fetchAllProjects(): Promise<PagedProjects> {
  let cursor: string | null = null;
  const items: PagedProjects['items'] = [];
  let totalCount: PagedProjects['totalCount'] = null;

  for (let page = 0; page < MaxPages; page += 1) {
    const query: string = cursor === null ? '' : `&cursor=${encodeURIComponent(cursor)}`;
    const next: PagedProjects = await apiFetch<PagedProjects>(`/api/v1/projects?limit=${PageSize}${query}`);

    items.push(...next.items);
    totalCount = next.totalCount ?? totalCount;
    cursor = next.nextCursor ?? null;

    if (cursor === null) break;
  }

  return { items, nextCursor: cursor, totalCount };
}
