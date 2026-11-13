/**
 * W-3 (рішення PO 11.11): сторінки курсорної видачі як одна. Рядки — підряд, без дублів за ключем (рядок, що між
 * підвантаженнями перейшов на іншу сторінку, не дає двох вузлів з одним `key`); `nextCursor` і решта полів — ОСТАННЬОЇ
 * сторінки, `totalCount` — ПЕРШОЇ (журнали рахують його лише на першій, `GetCellChangesHandler.cs:84`).
 */
export function mergePages<TPage extends { readonly items: readonly unknown[]; readonly totalCount: number | null }>(
  pages: readonly TPage[] | undefined,
  keyOf?: (item: TPage['items'][number]) => string | number,
): TPage | undefined {
  if (pages === undefined) return undefined;
  const first = pages[0];
  const last = pages.at(-1);
  if (first === undefined || last === undefined) return undefined;

  const all = pages.flatMap<TPage['items'][number]>((page) => page.items);
  const seen = new Set<string | number>();
  const items =
    keyOf === undefined
      ? all
      : all.filter((item) => {
          const key = keyOf(item);
          if (seen.has(key)) return false;
          seen.add(key);
          return true;
        });

  return { ...last, items, totalCount: first.totalCount } as TPage;
}

/**
 * Наступний курсор для `getNextPageParam`. ⚠ Той самий курсор, що щойно прочитаний, — кінець: інакше сервер, що повернув
 * незмінний курсор, зациклив би автодовантаження. `last?.` — тести інших екранів мокають чужі адреси відповіддю `null`
 * (той самий захист, що `CollectionRunsPanel.tsx:140-147`).
 */
export function nextCursorOf(
  last: { readonly nextCursor: string | null } | null | undefined,
  lastParam: string | null,
): string | null {
  const next = last?.nextCursor ?? null;

  return next !== null && next === lastParam ? null : next;
}
